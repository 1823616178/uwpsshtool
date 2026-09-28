using System;
using System.Collections.Generic;
using SshTool.Core.Sessions;

namespace SshTool.Core.Lifecycle
{
    // W04（01-DESIGN §16.4）：后台断线通知的判定。只在「在线 → 掉线」时、应用在后台、
    // 用户没主动断开、也不是保活策略主动挂起时通知；同一会话 DedupeMs 内只通知一次。
    public sealed class DisconnectNotifyPolicy
    {
        public const long DedupeMs = 60000;

        private readonly Dictionary<string, long> _lastNotified = new Dictionary<string, long>(StringComparer.Ordinal);

        public bool ShouldNotify(
            string sessionId,
            SessionUiState previous,
            SessionUiState next,
            bool enabled,
            bool inBackground,
            bool userClosed,
            bool policySuspended,
            long nowMs)
        {
            if (!enabled || !inBackground || userClosed || policySuspended || string.IsNullOrEmpty(sessionId))
            {
                return false;
            }
            if (previous != SessionUiState.Connected || !IsDropped(next))
            {
                return false;
            }
            long last;
            if (_lastNotified.TryGetValue(sessionId, out last) && nowMs - last < DedupeMs)
            {
                return false;
            }
            _lastNotified[sessionId] = nowMs;
            return true;
        }

        public void Forget(string sessionId)
        {
            if (sessionId != null)
            {
                _lastNotified.Remove(sessionId);
            }
        }

        private static bool IsDropped(SessionUiState state)
        {
            return state == SessionUiState.Reconnecting
                || state == SessionUiState.Disconnected
                || state == SessionUiState.Error;
        }
    }
}
