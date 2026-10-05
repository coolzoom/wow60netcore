using Client.World;
using Engine.Rendering;
using Silk.NET.OpenGL;

namespace Client.Online;

/// <summary>
/// Plays M2 animations for the units in view: a looping base animation chosen each frame (stand, run, dead, ...)
/// with one-shots (attacks, spell casts, being hit, dying) layered on top until they finish.
/// </summary>
public sealed class UnitAnimator(GL gl, AssetCache assets) : IDisposable
{
    public const int CombatWound = 9, Walkbackwards = 13, Attack2H = 18, ChannelCastOmni = 125, SitGround = 97,
        Sleep = 100, KneelLoop = 115;
    private const float FullRateDistance = 40f, CullDistance = 120f;

    private sealed class State(SkinnedActor actor, string model)
    {
        public SkinnedActor Actor { get; } = actor;
        public string Model { get; } = model;
        public int Animation = -1;
        public int Sequence;
        public long Start;
        public int OneShot = -1;
        public int OneShotSequence;
        public long OneShotStart;
        /// <summary>The one-shot ends on its last frame instead of returning to the base animation (dying).</summary>
        public bool Hold;
        public bool Dead;
        public bool Posed;
    }

    private readonly Dictionary<ulong, State> _states = [];
    private readonly HashSet<ulong> _seen = [];
    private int _frame;

    /// <summary>Call once per frame before posing; actors not posed since the previous call are released.</summary>
    public void BeginFrame()
    {
        _frame++;
        foreach (var guid in _states.Keys.Where(g => !_seen.Contains(g)).ToList())
            Forget(guid);
        _seen.Clear();
    }

    /// <summary>
    /// The posed actor for a unit, or null while its model or skeleton is still loading (draw the static model then).
    /// </summary>
    public SkinnedActor? Pose(ulong guid, string model, int baseAnimation, bool dead, float distance, long nowMs)
    {
        _seen.Add(guid);
        if (!_states.TryGetValue(guid, out var state) || !state.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            if (state is not null)
                Forget(guid);
            if (!assets.TryGetModelData(model, out var data) || data is null || assets.Skeleton(model) is not { Bones.Count: > 0 } skeleton)
                return null;
            state = new State(new SkinnedActor(gl, data, skeleton), model) { Dead = dead };
            _states[guid] = state;
            // Already a corpse when it came into view: lie at the end of the death animation.
            if (dead)
                Play(state, SkinnedActor.Death, SkinnedActor.Dead, nowMs - 1_000_000, hold: true);
        }

        if (dead && !state.Dead)
            Play(state, SkinnedActor.Death, SkinnedActor.Dead, nowMs, hold: true);
        else if (!dead && state.Dead)
            state.OneShot = -1;
        state.Dead = dead;

        if (state.Animation != baseAnimation)
        {
            state.Animation = baseAnimation;
            state.Sequence = state.Actor.Resolve(baseAnimation, Fallback(baseAnimation));
            state.Start = nowMs;
        }

        if (distance > CullDistance && state.Posed)
            return state.Actor;
        if (distance > FullRateDistance && state.Posed && (_frame + (int)(guid & 3)) % 4 != 0)
            return state.Actor;

        var sequence = state.Sequence;
        var elapsed = (uint)Math.Max(0, nowMs - state.Start);
        if (state.OneShot >= 0)
        {
            var length = state.Actor.Length(state.OneShotSequence);
            var oneShotElapsed = (uint)Math.Max(0, nowMs - state.OneShotStart);
            if (oneShotElapsed < length)
                (sequence, elapsed) = (state.OneShotSequence, oneShotElapsed);
            else if (state.Hold)
                (sequence, elapsed) = (state.OneShotSequence, length - 1);
            else
                state.OneShot = -1;
        }
        state.Actor.Update(sequence, elapsed, (uint)nowMs);
        state.Posed = true;
        return state.Actor;
    }

    /// <summary>Plays an animation once on a unit (if its actor exists), then returns to its base animation.</summary>
    public void PlayOnce(ulong guid, int animation, long nowMs, int fallback = -1)
    {
        if (_states.TryGetValue(guid, out var state) && !state.Dead)
            Play(state, animation, fallback, nowMs, hold: false);
    }

    public bool IsPlayingOnce(ulong guid) => _states.TryGetValue(guid, out var s) && s.OneShot >= 0 && !s.Hold;

    private static void Play(State state, int animation, int fallback, long nowMs, bool hold)
    {
        var index = state.Actor.Skeleton.FindSequence(animation);
        if (index < 0 && fallback >= 0)
            index = state.Actor.Skeleton.FindSequence(fallback);
        if (index < 0)
            return;
        state.OneShot = animation;
        state.OneShotSequence = index;
        state.OneShotStart = nowMs;
        state.Hold = hold;
    }

    /// <summary>What to play when a model lacks an animation.</summary>
    private static int Fallback(int animation) => animation switch
    {
        SkinnedActor.Walk or Walkbackwards => SkinnedActor.Run,
        SkinnedActor.Run => SkinnedActor.Walk,
        SkinnedActor.Ready1H or Attack2H => SkinnedActor.ReadyUnarmed,
        ChannelCastOmni => SkinnedActor.ReadySpellOmni,
        SkinnedActor.ReadySpellOmni => SkinnedActor.SpellPrecast,
        SkinnedActor.Swim => SkinnedActor.Run,
        SkinnedActor.Fall => SkinnedActor.Jump,
        _ => SkinnedActor.Stand,
    };

    public void Forget(ulong guid)
    {
        if (_states.Remove(guid, out var state))
            state.Actor.Dispose();
    }

    public void Dispose()
    {
        foreach (var state in _states.Values)
            state.Actor.Dispose();
        _states.Clear();
    }
}
