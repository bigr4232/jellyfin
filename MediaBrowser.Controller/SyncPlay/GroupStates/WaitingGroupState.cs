#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.SyncPlay.GroupStates
{
    /// <summary>
    /// Class WaitingGroupState.
    /// </summary>
    /// <remarks>
    /// Class is not thread-safe, external locking is required when accessing methods.
    /// </remarks>
    public class WaitingGroupState : AbstractGroupState
    {
        /// <summary>
        /// Maximum corrective seeks before a session is left behind rather than looped on.
        /// </summary>
        private const int MaxCorrectionAttempts = 5;

        /// <summary>
        /// How long the group waits for Ready reports after a seek before proceeding without them.
        /// </summary>
        /// <remarks>
        /// A seek leaves the item loaded, so a client that is going to answer answers at once.
        /// The stock web client does not answer at all when the seek lands inside the buffered
        /// range: its player never fires the event that its Ready report hangs off, so nothing is
        /// sent and the group would wait forever. See finding #24 in syncplay-code-review.md.
        /// </remarks>
        private static readonly TimeSpan SeekWaitTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long the group waits for Ready reports in every other case.
        /// </summary>
        /// <remarks>
        /// Loading a new item, joining, or recovering from a real buffering stall legitimately
        /// takes seconds. This matches the web client's own WaitForEventDefaultTimeout, so the
        /// group outlives the client's internal give-up rather than racing it.
        /// </remarks>
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<WaitingGroupState> _logger;

        /// <summary>
        /// Number of corrective seeks issued per session during this waiting cycle.
        /// </summary>
        private readonly Dictionary<string, int> _correctionAttempts = new();

        /// <summary>
        /// A session of this group, used as the sender when the wait times out.
        /// </summary>
        /// <remarks>
        /// The resume and pause broadcasts go to the whole group, which ignores the sender, so
        /// any participant will do; this just keeps a real one on hand.
        /// </remarks>
        private SessionInfo _waitingSession;

        /// <summary>
        /// Whether this waiting cycle was started by a seek, leaving the item already loaded.
        /// </summary>
        private bool _startedBySeek;

        /// <summary>
        /// Whether any session has reported buffering or readiness during this waiting cycle.
        /// </summary>
        private bool _sessionReported;

        /// <summary>
        /// Initializes a new instance of the <see cref="WaitingGroupState"/> class.
        /// </summary>
        /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
        public WaitingGroupState(ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
            _logger = LoggerFactory.CreateLogger<WaitingGroupState>();
        }

        /// <inheritdoc />
        public override GroupStateType Type { get; } = GroupStateType.Waiting;

        /// <summary>
        /// Gets or sets a value indicating whether playback should resume when group is ready.
        /// </summary>
        public bool ResumePlaying { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the initial state has been set.
        /// </summary>
        private bool InitialStateSet { get; set; } = false;

        /// <summary>
        /// Gets or sets the group state before the first ever event.
        /// </summary>
        private GroupStateType InitialState { get; set; }

        /// <inheritdoc />
        public override void SessionJoined(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            if (prevState.Equals(GroupStateType.Playing))
            {
                ResumePlaying = true;
                // Pause group and compute the media playback position.
                var currentTime = DateTime.UtcNow;
                var elapsedTime = currentTime - context.LastActivity;
                context.LastActivity = currentTime;
                // Elapsed time is negative if event happens
                // during the delay added to account for latency.
                // In this phase clients haven't started the playback yet.
                // In other words, LastActivity is in the future,
                // when playback unpause is supposed to happen.
                // Seek only if playback actually started.
                context.PositionTicks += Math.Max(elapsedTime.Ticks, 0);
            }

            // Prepare new session.
            var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist);
            var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
            context.SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, update, cancellationToken);

            context.SetBuffering(session, true);

            // Send pause command to all non-buffering sessions.
            var command = context.NewSyncPlayCommand(SendCommandType.Pause);
            context.SendCommand(session, SyncPlayBroadcastType.AllReady, command, cancellationToken);

            ArmWaitTimeout(context, session);
        }

        /// <inheritdoc />
        public override void SessionLeaving(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            context.SetBuffering(session, false);

            if (!context.IsBuffering())
            {
                if (ResumePlaying)
                {
                    _logger.LogDebug("Session {SessionId} left group {GroupId}, notifying others to resume.", session.Id, context.GroupId.ToString());

                    // Client, that was buffering, left the group.
                    var playingState = new PlayingGroupState(LoggerFactory);
                    context.SetState(playingState);
                    var unpauseRequest = new UnpauseGroupRequest();
                    playingState.HandleRequest(unpauseRequest, context, Type, session, cancellationToken);
                }
                else
                {
                    _logger.LogDebug("Session {SessionId} left group {GroupId}, returning to previous state.", session.Id, context.GroupId.ToString());

                    // Group is ready, returning to previous state.
                    var pausedState = new PausedGroupState(LoggerFactory);
                    context.SetState(pausedState);
                }
            }
            else
            {
                // Still waiting on someone else. The departing session is not offered as the
                // deadline sender: the timeout broadcasts to the whole group, which resolves its
                // own recipients, so a session that is on its way out would do no harm but adds
                // nothing either.
                ArmWaitTimeout(context, null);
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PlayGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            ResumePlaying = true;

            var setQueueStatus = context.SetPlayQueue(request.PlayingQueue, request.PlayingItemPosition, request.StartPositionTicks);
            if (!setQueueStatus)
            {
                _logger.LogError("Unable to set playing queue in group {GroupId}.", context.GroupId.ToString());

                // Ignore request and return to previous state.
                IGroupState newState = prevState switch
                {
                    GroupStateType.Playing => new PlayingGroupState(LoggerFactory),
                    GroupStateType.Paused => new PausedGroupState(LoggerFactory),
                    _ => new IdleGroupState(LoggerFactory)
                };

                context.SetState(newState);
                return;
            }

            var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist);
            var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
            context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

            // Reset status of sessions and await for all Ready events.
            context.SetAllBuffering(true);

            ArmWaitTimeout(context, session);

            _logger.LogDebug("Session {SessionId} set a new play queue in group {GroupId}.", session.Id, context.GroupId.ToString());
        }

        /// <inheritdoc />
        public override void HandleRequest(SetPlaylistItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            ResumePlaying = true;

            var result = context.SetPlayingItem(request.PlaylistItemId);
            if (result)
            {
                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.SetCurrentItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);

                ArmWaitTimeout(context, session);
            }
            else
            {
                // Return to old state.
                IGroupState newState = prevState switch
                {
                    GroupStateType.Playing => new PlayingGroupState(LoggerFactory),
                    GroupStateType.Paused => new PausedGroupState(LoggerFactory),
                    _ => new IdleGroupState(LoggerFactory)
                };

                context.SetState(newState);

                _logger.LogDebug("Unable to change current playing item in group {GroupId}.", context.GroupId.ToString());
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(UnpauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            if (prevState.Equals(GroupStateType.Idle))
            {
                ResumePlaying = true;
                context.RestartCurrentItem();

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);

                ArmWaitTimeout(context, session);

                _logger.LogDebug("Group {GroupId} is waiting for all ready events.", context.GroupId.ToString());
            }
            else
            {
                if (ResumePlaying)
                {
                    _logger.LogDebug("Forcing the playback to start in group {GroupId}. Group-wait is disabled until next state change.", context.GroupId.ToString());

                    // An Unpause request is forcing the playback to start, ignoring sessions that are not ready.
                    context.SetAllBuffering(false);

                    // Change state.
                    var playingState = new PlayingGroupState(LoggerFactory)
                    {
                        IgnoreBuffering = true
                    };
                    context.SetState(playingState);
                    playingState.HandleRequest(request, context, Type, session, cancellationToken);
                }
                else
                {
                    // Group would have gone to paused state, now will go to playing state when ready.
                    ResumePlaying = true;

                    // Notify relevant state change event.
                    SendGroupStateUpdate(context, request, session, cancellationToken);

                    ArmWaitTimeout(context, session);
                }
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            // Wait for sessions to be ready, then switch to paused state.
            ResumePlaying = false;

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);

            ArmWaitTimeout(context, session);
        }

        /// <inheritdoc />
        public override void HandleRequest(StopGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            // Change state.
            var idleState = new IdleGroupState(LoggerFactory);
            context.SetState(idleState);
            idleState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(SeekGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            if (prevState.Equals(GroupStateType.Playing))
            {
                ResumePlaying = true;

                // This seek is what put the group into the waiting state, so the item is already
                // loaded and the short deadline applies. A seek arriving while the group is
                // already waiting leaves the flag as the cycle set it.
                _startedBySeek = true;
            }
            else if (prevState.Equals(GroupStateType.Paused))
            {
                ResumePlaying = false;
                _startedBySeek = true;
            }

            // Sanitize PositionTicks.
            var ticks = context.SanitizePositionTicks(request.PositionTicks);

            // Seek.
            context.PositionTicks = ticks;
            context.LastActivity = DateTime.UtcNow;

            var command = context.NewSyncPlayCommand(SendCommandType.Seek);
            context.SendCommand(session, SyncPlayBroadcastType.AllGroup, command, cancellationToken);

            // Reset status of sessions and await for all Ready events.
            context.SetAllBuffering(true);

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);

            ArmWaitTimeout(context, session);
        }

        /// <inheritdoc />
        public override void HandleRequest(BufferGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            // A session reporting anything means the group is not waiting on a client that
            // has silently finished, so the short seek deadline no longer applies.
            _sessionReported = true;

            // Make sure the client is playing the correct item.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} reported wrong playlist item in group {GroupId}.", session.Id, context.GroupId.ToString());

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.SetCurrentItem);
                var updateSession = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, updateSession, cancellationToken);
                context.SetBuffering(session, true);

                ArmWaitTimeout(context, session);

                return;
            }

            if (prevState.Equals(GroupStateType.Playing))
            {
                // Resume playback when all ready.
                ResumePlaying = true;

                context.SetBuffering(session, true);

                // Pause group and compute the media playback position.
                var currentTime = DateTime.UtcNow;
                var elapsedTime = currentTime - context.LastActivity;
                context.LastActivity = currentTime;
                // Elapsed time is negative if event happens
                // during the delay added to account for latency.
                // In this phase clients haven't started the playback yet.
                // In other words, LastActivity is in the future,
                // when playback unpause is supposed to happen.
                // Seek only if playback actually started.
                context.PositionTicks += Math.Max(elapsedTime.Ticks, 0);

                // Send pause command to all non-buffering sessions.
                var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                context.SendCommand(session, SyncPlayBroadcastType.AllReady, command, cancellationToken);
            }
            else if (prevState.Equals(GroupStateType.Paused))
            {
                // Don't resume playback when all ready.
                ResumePlaying = false;

                context.SetBuffering(session, true);

                // Send pause command to buffering session.
                var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
            }
            else if (prevState.Equals(GroupStateType.Waiting))
            {
                // Another session is now buffering.
                context.SetBuffering(session, true);

                if (!ResumePlaying)
                {
                    // Force update for this session that should be paused.
                    var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
                }
            }

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);

            ArmWaitTimeout(context, session);
        }

        /// <inheritdoc />
        public override void HandleRequest(ReadyGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            _sessionReported = true;

            // Make sure the client is playing the correct item.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} reported wrong playlist item in group {GroupId}.", session.Id, context.GroupId.ToString());

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.SetCurrentItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, update, cancellationToken);
                context.SetBuffering(session, true);

                ArmWaitTimeout(context, session);

                return;
            }

            // Compute elapsed time between the client reported time and now.
            // Elapsed time is used to estimate the client position when playback is unpaused.
            // Ideally, the request is received and handled without major delays.
            // However, to avoid waiting indefinitely when a client is not reporting a correct time,
            // the elapsed time is ignored after a certain threshold.
            var currentTime = DateTime.UtcNow;
            var elapsedTime = currentTime.Subtract(request.When);
            var timeSyncThresholdTicks = TimeSpan.FromMilliseconds(context.TimeSyncOffset).Ticks;
            if (Math.Abs(elapsedTime.Ticks) > timeSyncThresholdTicks)
            {
                _logger.LogWarning("Session {SessionId} is not time syncing properly. Ignoring elapsed time.", session.Id);

                elapsedTime = TimeSpan.Zero;
            }

            // Ignore elapsed time if client is paused.
            if (!request.IsPlaying)
            {
                elapsedTime = TimeSpan.Zero;
            }

            var requestTicks = context.SanitizePositionTicks(request.PositionTicks);
            var clientPosition = TimeSpan.FromTicks(requestTicks) + elapsedTime;
            var delayTicks = context.PositionTicks - clientPosition.Ticks;
            var maxPlaybackOffsetTicks = TimeSpan.FromMilliseconds(context.MaxPlaybackOffset).Ticks;

            _logger.LogDebug("Session {SessionId} is at {PositionTicks} (delay of {Delay} seconds) in group {GroupId}.", session.Id, clientPosition, TimeSpan.FromTicks(delayTicks).TotalSeconds, context.GroupId.ToString());

            if (ResumePlaying)
            {
                // Handle case where session reported as ready but in reality
                // it has no clue of the real position nor the playback state.
                if (!request.IsPlaying && Math.Abs(delayTicks) > maxPlaybackOffsetTicks)
                {
                    var attempts = RegisterCorrectionAttempt(session.Id);
                    if (attempts <= MaxCorrectionAttempts)
                    {
                        // Session not ready at all.
                        context.SetBuffering(session, true);

                        // Correcting session's position.
                        var command = context.NewSyncPlayCommand(SendCommandType.Seek);
                        context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

                        // Notify relevant state change event.
                        SendGroupStateUpdate(context, request, session, cancellationToken);

                        ArmWaitTimeout(context, session);

                        _logger.LogWarning("Session {SessionId} got lost in time, correcting.", session.Id);
                        return;
                    }

                    // The corrections are not getting through: the client cannot reach the
                    // group position (slow transcode, bad clock, unseekable stream).
                    // Treat it as ready so the group is not held back by it.
                    _logger.LogWarning(
                        "Session {SessionId} is still lost in time after {Attempts} corrections in group {GroupId}; proceeding without it.",
                        session.Id,
                        attempts,
                        context.GroupId.ToString());
                }

                // Session reached the group position, so it gets a fresh budget of
                // corrections should it drift again. Checked independently of the
                // branch above: this is a property of where the session is, not of
                // which correction path ran. A session that just gave up is excluded
                // by the tolerance, so giving up never refreshes its own budget.
                if (Math.Abs(delayTicks) <= maxPlaybackOffsetTicks)
                {
                    _correctionAttempts.Remove(session.Id);
                }

                // Session is ready.
                context.SetBuffering(session, false);

                if (context.IsBuffering())
                {
                    // A delay negative by more than the playback offset means this
                    // client is *ahead* of the group. Scheduling a command in the
                    // past makes the client fire it immediately and land further out
                    // of position, so correct it. Smaller negative delays are the
                    // one-way latency of the Ready report, not a real desync.
                    if (delayTicks < -maxPlaybackOffsetTicks)
                    {
                        var attempts = RegisterCorrectionAttempt(session.Id);
                        if (attempts <= MaxCorrectionAttempts)
                        {
                            // Session is ahead of the group, put it back to buffering
                            // while it seeks to the group position.
                            context.SetBuffering(session, true);

                            var seek = context.NewSyncPlayCommand(SendCommandType.Seek);
                            context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, seek, cancellationToken);

                            // Notify relevant state change event.
                            SendGroupStateUpdate(context, request, session, cancellationToken);

                            ArmWaitTimeout(context, session);

                            _logger.LogWarning("Session {SessionId} is ahead of group {GroupId} by {Delay} seconds, correcting.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(-delayTicks).TotalSeconds);
                            return;
                        }

                        // The client cannot seek back to the group position (slow transcode,
                        // bad clock, unseekable stream). Stop correcting: looping starves it
                        // further and restarts its transcode on every seek. Pause it where it
                        // is instead, so it stops drifting further ahead while the group
                        // waits, and leave it out of the buffering set so the group proceeds.
                        _logger.LogWarning(
                            "Session {SessionId} is still ahead of group {GroupId} after {Attempts} corrections; pausing it in place.",
                            session.Id,
                            context.GroupId.ToString(),
                            attempts);

                        delayTicks = 0;
                    }
                    else if (delayTicks > maxPlaybackOffsetTicks)
                    {
                        // Behind the group by more than the tolerance while someone else is still
                        // buffering. "Pause when ready in N seconds" is the upstream design for a
                        // client lagging by a few hundred milliseconds; past that the session is
                        // not lagging, it is in the wrong place, and scheduling its pause that far
                        // out means it never pauses at all. Correct it under the shared budget,
                        // the same way the last-one-ready branch below does.
                        var attempts = RegisterCorrectionAttempt(session.Id);
                        if (attempts <= MaxCorrectionAttempts)
                        {
                            context.SetBuffering(session, true);

                            var seek = context.NewSyncPlayCommand(SendCommandType.Seek);
                            context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, seek, cancellationToken);

                            // Notify relevant state change event.
                            SendGroupStateUpdate(context, request, session, cancellationToken);

                            ArmWaitTimeout(context, session);

                            _logger.LogWarning("Session {SessionId} is behind group {GroupId} by {Delay} seconds while others buffer, correcting.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(delayTicks).TotalSeconds);
                            return;
                        }

                        // The client cannot reach the group position. Stop correcting and pause it
                        // where it is, so it stops drifting further behind while the group waits.
                        _logger.LogWarning(
                            "Session {SessionId} is still behind group {GroupId} after {Attempts} corrections; pausing it in place.",
                            session.Id,
                            context.GroupId.ToString(),
                            attempts);

                        delayTicks = 0;
                    }

                    // Clamp sub-tolerance jitter so the pause is never dated in
                    // the past, which the client would fire immediately.
                    delayTicks = Math.Max(delayTicks, 0);

                    // Others are still buffering, tell this client to pause when ready.
                    var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                    command.When = currentTime.AddTicks(delayTicks);
                    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

                    ArmWaitTimeout(context, session);

                    _logger.LogInformation("Session {SessionId} will pause when ready in {Delay} seconds. Group {GroupId} is waiting for all ready events.", session.Id, TimeSpan.FromTicks(delayTicks).TotalSeconds, context.GroupId.ToString());
                }
                else
                {
                    // If all ready, then start playback.
                    // Let other clients resume as soon as the buffering client catches up.
                    if (delayTicks > maxPlaybackOffsetTicks)
                    {
                        // The session is not lagging, it is in the wrong place
                        // (a rejoin at position 0, a seek that never landed).
                        // Correct it under the shared correction budget rather
                        // than scheduling the group's resume arbitrarily far ahead.
                        var attempts = RegisterCorrectionAttempt(session.Id);
                        if (attempts <= MaxCorrectionAttempts)
                        {
                            // Session is behind the group, put it back to buffering
                            // while it seeks to the group position.
                            context.SetBuffering(session, true);

                            var seek = context.NewSyncPlayCommand(SendCommandType.Seek);
                            context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, seek, cancellationToken);

                            // Notify relevant state change event.
                            SendGroupStateUpdate(context, request, session, cancellationToken);

                            ArmWaitTimeout(context, session);

                            _logger.LogWarning("Session {SessionId} is behind group {GroupId} by {Delay} seconds, correcting.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(delayTicks).TotalSeconds);
                            return;
                        }

                        // The client cannot reach the group position (slow transcode,
                        // bad clock, unseekable stream). Stop correcting: looping starves
                        // it further and restarts its transcode on every seek. Leave it
                        // behind and resume the group with the standard recovery delay,
                        // not its full gap.
                        _logger.LogWarning(
                            "Session {SessionId} is still behind group {GroupId} after {Attempts} corrections; proceeding without it.",
                            session.Id,
                            context.GroupId.ToString(),
                            attempts);

                        delayTicks = 0;
                    }

                    if (delayTicks > context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond)
                    {
                        // Client that was buffering is recovering, notifying others to resume.
                        context.LastActivity = currentTime.AddTicks(delayTicks);
                        var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                        var filter = SyncPlayBroadcastType.AllExceptCurrentSession;
                        if (!request.IsPlaying)
                        {
                            filter = SyncPlayBroadcastType.AllGroup;
                        }

                        context.SendCommand(session, filter, command, cancellationToken);

                        _logger.LogInformation("Session {SessionId} is recovering, group {GroupId} will resume in {Delay} seconds.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(delayTicks).TotalSeconds);
                    }
                    else
                    {
                        // Client, that was buffering, resumed playback but did not update others in time.
                        delayTicks = context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond;
                        delayTicks = Math.Max(delayTicks, TimeSpan.FromMilliseconds(context.DefaultPing).Ticks);

                        context.LastActivity = currentTime.AddTicks(delayTicks);

                        var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                        context.SendCommand(session, SyncPlayBroadcastType.AllGroup, command, cancellationToken);

                        _logger.LogWarning("Session {SessionId} resumed playback, group {GroupId} has {Delay} seconds to recover.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(delayTicks).TotalSeconds);
                    }

                    // Change state.
                    var playingState = new PlayingGroupState(LoggerFactory);
                    context.SetState(playingState);
                    playingState.HandleRequest(request, context, Type, session, cancellationToken);
                }
            }
            else
            {
                // Check that session is really ready, tolerate player imperfections under a certain threshold.
                if (Math.Abs(context.PositionTicks - requestTicks) > maxPlaybackOffsetTicks)
                {
                    var attempts = RegisterCorrectionAttempt(session.Id);
                    if (attempts <= MaxCorrectionAttempts)
                    {
                        // Session still not ready.
                        context.SetBuffering(session, true);
                        // Session is seeking to wrong position, correcting.
                        var command = context.NewSyncPlayCommand(SendCommandType.Seek);
                        context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

                        // Notify relevant state change event.
                        SendGroupStateUpdate(context, request, session, cancellationToken);

                        ArmWaitTimeout(context, session);

                        _logger.LogWarning("Session {SessionId} is seeking to wrong position, correcting.", session.Id);
                        return;
                    }

                    // The client cannot reach the group position (slow transcode, bad clock,
                    // unseekable stream). Stop correcting: looping starves it further and
                    // restarts its transcode on every seek. It is treated as ready from here
                    // on, desynced, so that the group is not held in the waiting state by it.
                    _logger.LogWarning(
                        "Session {SessionId} failed to reach position after {Attempts} corrections in group {GroupId}; proceeding without it.",
                        session.Id,
                        attempts,
                        context.GroupId.ToString());
                }
                else
                {
                    // Session reached the group position, so it gets a fresh budget
                    // of corrections should it drift again.
                    _correctionAttempts.Remove(session.Id);
                }

                // Session is ready.
                context.SetBuffering(session, false);

                if (!context.IsBuffering())
                {
                    _logger.LogDebug("Session {SessionId} is ready, group {GroupId} is ready.", session.Id, context.GroupId.ToString());

                    // Group is ready, returning to previous state.
                    var pausedState = new PausedGroupState(LoggerFactory);
                    context.SetState(pausedState);

                    if (InitialState.Equals(GroupStateType.Playing))
                    {
                        // Group went from playing to waiting state and a pause request occurred while waiting.
                        var pauseRequest = new PauseGroupRequest();
                        pausedState.HandleRequest(pauseRequest, context, Type, session, cancellationToken);
                    }
                    else if (InitialState.Equals(GroupStateType.Paused))
                    {
                        pausedState.HandleRequest(request, context, Type, session, cancellationToken);
                    }
                }
                else
                {
                    ArmWaitTimeout(context, session);
                }
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(NextItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            ResumePlaying = true;

            // Make sure the client knows the playing item, to avoid duplicate requests.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} provided the wrong playlist item for group {GroupId}.", session.Id, context.GroupId.ToString());
                return;
            }

            var newItem = context.NextItemInQueue();
            if (newItem)
            {
                // Send playing-queue update.
                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NextItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);

                ArmWaitTimeout(context, session);
            }
            else
            {
                // Return to old state.
                IGroupState newState = prevState switch
                {
                    GroupStateType.Playing => new PlayingGroupState(LoggerFactory),
                    GroupStateType.Paused => new PausedGroupState(LoggerFactory),
                    _ => new IdleGroupState(LoggerFactory)
                };

                context.SetState(newState);

                _logger.LogDebug("No next item available in group {GroupId}.", context.GroupId.ToString());
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PreviousItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Save state if first event.
            if (!InitialStateSet)
            {
                InitialState = prevState;
                InitialStateSet = true;
            }

            ResumePlaying = true;

            // Make sure the client knows the playing item, to avoid duplicate requests.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} provided the wrong playlist item for group {GroupId}.", session.Id, context.GroupId.ToString());
                return;
            }

            var newItem = context.PreviousItemInQueue();
            if (newItem)
            {
                // Send playing-queue update.
                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.PreviousItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);

                ArmWaitTimeout(context, session);
            }
            else
            {
                // Return to old state.
                IGroupState newState = prevState switch
                {
                    GroupStateType.Playing => new PlayingGroupState(LoggerFactory),
                    GroupStateType.Paused => new PausedGroupState(LoggerFactory),
                    _ => new IdleGroupState(LoggerFactory)
                };

                context.SetState(newState);

                _logger.LogDebug("No previous item available in group {GroupId}.", context.GroupId.ToString());
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(IgnoreWaitGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            context.SetIgnoreGroupWait(session, request.IgnoreWait);

            if (!context.IsBuffering())
            {
                _logger.LogDebug("Ignoring session {SessionId}, group {GroupId} is ready.", session.Id, context.GroupId.ToString());

                if (ResumePlaying)
                {
                    // Client, that was buffering, stopped following playback.
                    var playingState = new PlayingGroupState(LoggerFactory);
                    context.SetState(playingState);
                    var unpauseRequest = new UnpauseGroupRequest();
                    playingState.HandleRequest(unpauseRequest, context, Type, session, cancellationToken);
                }
                else
                {
                    // Group is ready, returning to previous state.
                    var pausedState = new PausedGroupState(LoggerFactory);
                    context.SetState(pausedState);
                }
            }
            else
            {
                ArmWaitTimeout(context, session);
            }
        }

        /// <inheritdoc />
        public override void OnStateTimeout(IGroupStateContext context, CancellationToken cancellationToken)
        {
            if (!context.IsBuffering())
            {
                // The group converged between the deadline elapsing and this running.
                return;
            }

            if (_waitingSession is null)
            {
                // Nothing to broadcast from. Cannot happen in practice: every path that arms the
                // deadline passes a session, and the one that does not runs after those.
                _logger.LogWarning("Group {GroupId} timed out waiting with no known session.", context.GroupId.ToString());
                return;
            }

            _logger.LogWarning(
                "Group {GroupId} timed out waiting for Ready reports; proceeding without the sessions that did not answer.",
                context.GroupId.ToString());

            context.SetAllBuffering(false);

            // This is the automatic equivalent of a participant pressing play to escape the wait,
            // minus the IgnoreBuffering flag that the manual Unpause path sets: real buffering
            // reports must keep working after the group resumes.
            if (ResumePlaying)
            {
                var playingState = new PlayingGroupState(LoggerFactory);
                context.SetState(playingState);
                playingState.HandleRequest(new UnpauseGroupRequest(), context, Type, _waitingSession, cancellationToken);
            }
            else
            {
                // LastActivity is still dated at the start of the waiting cycle, and the paused
                // state folds the time since into PositionTicks as if it had been playing.
                context.LastActivity = DateTime.UtcNow;

                var pausedState = new PausedGroupState(LoggerFactory);
                context.SetState(pausedState);
                pausedState.HandleRequest(new PauseGroupRequest(), context, Type, _waitingSession, cancellationToken);
            }
        }

        /// <summary>
        /// Schedules the deadline by which this waiting cycle gives up on the sessions that have
        /// not reported, replacing any deadline already pending.
        /// </summary>
        /// <param name="context">The context of the state.</param>
        /// <param name="session">A live session of the group, or <c>null</c> to keep the current one.</param>
        private void ArmWaitTimeout(IGroupStateContext context, SessionInfo session)
        {
            if (session is not null)
            {
                _waitingSession = session;
            }

            var timeout = _startedBySeek && !_sessionReported ? SeekWaitTimeout : WaitTimeout;
            context.ScheduleStateTimeout(timeout);
        }

        /// <summary>
        /// Registers a corrective action issued for a session during this waiting cycle.
        /// </summary>
        /// <param name="sessionId">The session identifier.</param>
        /// <returns>The number of corrections issued so far, including this one.</returns>
        private int RegisterCorrectionAttempt(string sessionId)
        {
            _correctionAttempts.TryGetValue(sessionId, out var attempts);
            attempts++;
            _correctionAttempts[sessionId] = attempts;

            return attempts;
        }
    }
}
