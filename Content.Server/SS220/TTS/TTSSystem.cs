using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Server.VoiceMask;
using Content.Shared.Corvax.CCCVars;
using Content.Shared.Inventory;
using Content.Shared.SS220.TTS;
using Content.Shared.GameTicking;
using Content.Shared.SS220.AnnounceTTS;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Serilog;

namespace Content.Server.SS220.TTS;

// ReSharper disable once InconsistentNaming
public sealed partial class TTSSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly TTSManager _ttsManager = default!;
    [Dependency] private readonly SharedTransformSystem _xforms = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly ILogManager _log = default!;

    private ISawmill _sawmill = default!;

    private const int MaxMessageChars = 100 * 2; // same as SingleBubbleCharLimit * 2
    private bool _isEnabled = false;
    private string _voiceId = "glados";
    public const float WhisperVoiceVolumeModifier = 0.6f; // how far whisper goes in world units
    public const int WhisperVoiceRange = 6; // how far whisper goes in world units

    public override void Initialize()
    {
        base.Initialize();
        _cfg.OnValueChanged(CCCVars.TTSEnabled, v => _isEnabled = v, true);
        _cfg.OnValueChanged(CCCVars.TTSAnnounceVoiceId, v => _voiceId = v, true);

        SubscribeLocalEvent<TransformSpeechEvent>(OnTransformSpeech);
        SubscribeLocalEvent<TTSComponent, EntitySpokeEvent>(OnEntitySpoke);
        SubscribeLocalEvent<RadioSpokeEvent>(OnRadioReceiveEvent);
        SubscribeLocalEvent<AnnouncementSpokeEvent>(OnAnnouncementSpoke);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<TTSComponent, MapInitEvent>(OnInit);

        SubscribeNetworkEvent<RequestGlobalTTSEvent>(OnRequestGlobalTTS);

        _sawmill = _log.GetSawmill("TTSSystem");
    }

    private void OnInit(Entity<TTSComponent> ent, ref MapInitEvent args)
    {
        // Set random voice from RandomVoicesList
        // If RandomVoicesList is null - doesn`t set new voice
        SetRandomVoice(ent);
    }

    private void OnRadioReceiveEvent(RadioSpokeEvent args)
    {
        if (!_isEnabled || args.Message.Length > MaxMessageChars)
            return;

        if (!TryComp(args.Source, out TTSComponent? senderComponent))
            return;

        var voiceId = senderComponent.VoicePrototypeId;
        if (voiceId == null)
            return;

        if (TryGetVoiceMaskUid(args.Source, out var maskUid))
        {
            var voiceEv = new TransformSpeakerVoiceEvent(maskUid.Value, voiceId);
            RaiseLocalEvent(maskUid.Value, voiceEv);
            voiceId = voiceEv.VoiceId;
        }

        if (!GetVoicePrototype(voiceId, out var protoVoice))
        {
            return;
        }

        HandleRadio(args.Receivers, args.Message, protoVoice.Speaker);
    }

    private bool GetVoicePrototype(string voiceId, [NotNullWhen(true)] out TTSVoicePrototype? voicePrototype)
    {
        if (!_prototypeManager.TryIndex(voiceId, out voicePrototype))
        {
            return _prototypeManager.TryIndex("father_grigori", out voicePrototype);
        }

        return true;
    }

    private void SetRandomVoice(EntityUid uid, TTSComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return;

        var protoId = comp.RandomVoicesList;
        if (protoId is null)
            return;

        comp.VoicePrototypeId = _random.Pick(_prototypeManager.Index<RandomVoicesListPrototype>(protoId).VoicesList);
    }

    private async void OnAnnouncementSpoke(AnnouncementSpokeEvent args)
    {
        var voice = args.SpokeVoiceId;

        if (string.IsNullOrWhiteSpace(voice))
        {
            if (GetVoicePrototype(_voiceId, out var protoVoice))
            {
                voice = protoVoice.Speaker;
            }
        }

        if (!_isEnabled ||
            args.Message.Length > MaxMessageChars * 2 ||
            string.IsNullOrWhiteSpace(voice))
        {
            RaiseNetworkEvent(new AnnounceTTSEvent([], args.AnnouncementSound, args.AnnouncementSoundParams), args.Source);
            return;
        }

        var soundData = await GenerateTTS(args.Message, voice, isAnnounce: true) ?? [];

        RaiseNetworkEvent(new AnnounceTTSEvent(soundData, args.AnnouncementSound, args.AnnouncementSoundParams), args.Source);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _ttsManager.ResetCache();
    }

    public bool TryGetVoiceMaskUid(EntityUid maskCarrier, [NotNullWhen(true)] out EntityUid? maskUid)
    {
        maskUid = null;
        if (!_inventory.TryGetContainerSlotEnumerator(maskCarrier, out var carrierSlot, SlotFlags.MASK))
            return false;

        while (carrierSlot.NextItem(out var itemUid, out var itemSlot))
        {
            if (HasComp<VoiceMaskComponent>(itemUid))
            {
                maskUid = itemUid;
                return true;
            }
        }
        return false;
    }

    private async void OnRequestGlobalTTS(RequestGlobalTTSEvent ev, EntitySessionEventArgs args)
    {
        if (!_isEnabled ||
            ev.Text.Length > MaxMessageChars ||
            !GetVoicePrototype(ev.VoiceId, out var protoVoice))
            return;

        var soundData = await GenerateTTS(ev.Text, protoVoice.Speaker);
        if (soundData is null)
            return;

        RaiseNetworkEvent(new PlayTTSEvent(soundData), Filter.SinglePlayer(args.SenderSession));
    }

    private async void OnEntitySpoke(EntityUid uid, TTSComponent component, EntitySpokeEvent args)
    {
        var voiceId = component.VoicePrototypeId;
        if (!_isEnabled ||
            args.Message.Length > MaxMessageChars ||
            voiceId == null)
            return;

        if (TryGetVoiceMaskUid(uid, out var maskUid))
        {
            var voiceEv = new TransformSpeakerVoiceEvent(maskUid.Value, voiceId);
            RaiseLocalEvent(maskUid.Value, voiceEv);
            voiceId = voiceEv.VoiceId;
        }

        if (!GetVoicePrototype(voiceId, out var protoVoice))
        {
            return;
        }

        if (args.ObfuscatedMessage != null)
        {
            HandleWhisper(uid, args.Message, args.ObfuscatedMessage, protoVoice.Speaker, args.IsRadio);
            return;
        }

        HandleSay(uid, args.Message, protoVoice.Speaker);
    }

    private async void HandleSay(EntityUid uid, string message, string speaker)
    {
        var soundData = await GenerateTTS(message, speaker);
        if (soundData is null) return;
        RaiseNetworkEvent(new PlayTTSEvent(soundData, GetNetEntity(uid)), Filter.Pvs(uid));
    }

    private async void HandleWhisper(EntityUid uid, string message, string obfMessage, string speaker, bool isRadio)
    {
        // If it's a whisper into a radio, generate speech without whisper
        // attributes to prevent an additional speech synthesis event
        var soundData = await GenerateTTS(message, speaker, isWhisper: true);
        if (soundData is null)
            return;

        var obfSoundData = await GenerateTTS(obfMessage, speaker, isWhisper: true);
        if (obfSoundData is null)
            return;

        // TODO: Check obstacles
        var xformQuery = GetEntityQuery<TransformComponent>();
        var sourcePos = _xforms.GetWorldPosition(xformQuery.GetComponent(uid), xformQuery);
        var receptions = Filter.Pvs(uid).Recipients;
        foreach (var session in receptions)
        {
            if (!session.AttachedEntity.HasValue)
                continue;

            var xform = xformQuery.GetComponent(session.AttachedEntity.Value);
            var distance = (sourcePos - _xforms.GetWorldPosition(xform, xformQuery)).Length();

            if (distance > ChatSystem.WhisperMuffledRange)
                continue;

            var fullTtsEvent = new PlayTTSEvent(
                soundData,
                GetNetEntity(uid),
                isWhisper: true);

            var obfTtsEvent = new PlayTTSEvent(obfSoundData, GetNetEntity(uid), isWhisper: true);

            RaiseNetworkEvent(distance > ChatSystem.WhisperClearRange ? obfTtsEvent : fullTtsEvent, session);
        }
    }

    private async void HandleRadio(RadioEventReceiver[] receivers, string message, string speaker)
    {
        var soundData = await GenerateTTS(message, speaker, false, true);
        if (soundData is null)
            return;

        foreach (var receiver in receivers)
        {
            RaiseNetworkEvent(new PlayTTSEvent(soundData, GetNetEntity(receiver.PlayTarget.EntityId), true), receiver.Actor);
        }
    }

    // ReSharper disable once InconsistentNaming
    private async Task<byte[]?> GenerateTTS(string text, string speaker, bool isWhisper = false, bool isRadio = false, bool isAnnounce = false)
    {
        try
        {
            var textSanitized = Sanitize(text);
            if (textSanitized == "") return null;
            if (char.IsLetter(textSanitized[^1]))
                textSanitized += ".";

            var ssmlTraits = SoundTraits.RateFast;
            if (isWhisper)
                ssmlTraits |= SoundTraits.PitchVerylow;

            var textSsml = ToSsmlText(textSanitized, ssmlTraits);

            return await _ttsManager.ConvertTextToSpeech(speaker, textSanitized, isRadio, isAnnounce);

            //return isRadio
            //    ? await _ttsManager.ConvertTextToSpeechRadio(speaker, textSanitized)
            //    : await _ttsManager.ConvertTextToSpeech(speaker, textSanitized, isRadio: false);
        }
        catch (Exception e)
        {
            // Catch TTS exceptions to prevent a server crash.
            _sawmill.Error($"TTS System error: {e.Message}");
        }

        return null;
    }
}

public sealed class TransformSpeakerVoiceEvent : EntityEventArgs
{
    public EntityUid Sender;
    public string VoiceId;

    public TransformSpeakerVoiceEvent(EntityUid sender, string voiceId)
    {
        Sender = sender;
        VoiceId = voiceId;
    }
}
