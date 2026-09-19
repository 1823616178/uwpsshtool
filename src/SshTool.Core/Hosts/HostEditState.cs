using System;
using System.Collections.Generic;
using SshTool.Core.Models;
using SshTool.Core.Validation;

namespace SshTool.Core.Hosts
{
    public enum HostEditMode
    {
        New = 0,
        Edit = 1,
        Duplicate = 2
    }

    public sealed class EnvVarRow
    {
        public EnvVarRow()
        {
        }

        public EnvVarRow(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; set; }
        public string Value { get; set; }
    }

    public sealed class HostEditState
    {
        private Host _baseline;

        public HostEditMode Mode { get; set; }
        public string HostId { get; set; }
        public string Name { get; set; }
        public string HostName { get; set; }
        public string PortText { get; set; }
        public string Username { get; set; }
        public AuthType AuthType { get; set; }
        public string GroupId { get; set; }
        public string KeepaliveText { get; set; }
        public string HostFingerprint { get; set; }
        public string AppearanceId { get; set; }
        public string TermType { get; set; }
        public bool BackspaceSendsCtrlH { get; set; }
        public string InitCommandsText { get; set; }
        public List<EnvVarRow> EnvVars { get; set; }
        public bool TmuxAutoAttach { get; set; }
        public string TmuxSessionName { get; set; }
        public string JumpHostId { get; set; }
        public string KeyId { get; set; }
        public int SortOrder { get; set; }
        public string LastConnectedAt { get; set; }

        public static HostEditState ForNew()
        {
            Host host = Defaults.NewHost();
            var state = FromHost(host, HostEditMode.New);
            state.CaptureBaseline();
            return state;
        }

        public static HostEditState ForEdit(Host host)
        {
            var state = FromHost(host.Clone(), HostEditMode.Edit);
            state.CaptureBaseline();
            return state;
        }

        public static HostEditState ForDuplicate(Host host)
        {
            Host clone = host.Clone();
            clone.Id = Common.IdGenerator.NewId();
            clone.Name = (host.Name ?? string.Empty) + " 副本";
            clone.LastConnectedAt = null;
            var state = FromHost(clone, HostEditMode.Duplicate);
            state.CaptureBaseline();
            return state;
        }

        public void CaptureBaseline()
        {
            _baseline = ToHost();
        }

        public bool IsDirty
        {
            get { return _baseline == null || !SameAs(_baseline); }
        }

        public ValidationResult Validate(IReadOnlyList<Host> allHosts)
        {
            Host host = ToHost();
            ValidationResult result = HostValidator.Validate(host);
            if (!TryParsePort(PortText))
            {
                result.Add("port", ValidationKeys.PortRange);
            }
            if (!TryParseKeepalive(KeepaliveText))
            {
                result.Add("keepalive", ValidationKeys.KeepaliveRange);
            }
            string envError;
            if (!TryParseEnv(out envError))
            {
                result.Add("envVars", ValidationKeys.EnvVarsFormat);
            }
            JumpChainStatus jump = JumpChainValidator.Validate(HostId, JumpHostId, allHosts);
            if (jump == JumpChainStatus.SelfLoop)
            {
                result.Add("jumpHostId", ValidationKeys.JumpSelfLoop);
            }
            else if (jump == JumpChainStatus.Cycle)
            {
                result.Add("jumpHostId", ValidationKeys.JumpCycle);
            }
            else if (jump == JumpChainStatus.TooDeep)
            {
                result.Add("jumpHostId", ValidationKeys.JumpTooDeep);
            }
            return result;
        }

        public Host ToHost()
        {
            Host host = _baseline != null ? _baseline.Clone() : Defaults.NewHost();
            host.Id = HostId ?? host.Id;
            host.Name = Name ?? string.Empty;
            host.HostName = HostName ?? string.Empty;
            int port;
            host.Port = TryParsePort(PortText, out port) ? port : 0;
            host.Username = Username ?? string.Empty;
            host.AuthType = AuthType;
            host.GroupId = EmptyToNull(GroupId);
            int keepalive;
            host.Keepalive = TryParseKeepalive(KeepaliveText, out keepalive) ? keepalive : -1;
            host.HostFingerprint = HostFingerprint ?? string.Empty;
            host.AppearanceId = EmptyToNull(AppearanceId);
            host.TermType = TermType ?? string.Empty;
            host.BackspaceSendsCtrlH = BackspaceSendsCtrlH;
            host.InitCommands = ParseInitCommands(InitCommandsText);
            Dictionary<string, string> env;
            string ignored;
            host.EnvVars = TryBuildEnv(out env, out ignored) ? env : new Dictionary<string, string>();
            host.TmuxAutoAttach = TmuxAutoAttach;
            host.TmuxSessionName = TmuxSessionName ?? string.Empty;
            host.JumpHostId = EmptyToNull(JumpHostId);
            host.KeyId = EmptyToNull(KeyId);
            host.SortOrder = SortOrder;
            host.LastConnectedAt = LastConnectedAt;
            return host;
        }

        public static int PivotIndexForField(string field)
        {
            if (field == "envVars" || field == "termType" || field == "appearanceId")
            {
                return 2;
            }
            if (field == "jumpHostId" || field == "initCommands" || field == "tmux")
            {
                return 3;
            }
            if (field == "authType")
            {
                return 1;
            }
            return 0;
        }

        private static HostEditState FromHost(Host host, HostEditMode mode)
        {
            var env = new List<EnvVarRow>();
            if (host.EnvVars != null)
            {
                foreach (var pair in host.EnvVars)
                {
                    env.Add(new EnvVarRow(pair.Key, pair.Value));
                }
            }
            return new HostEditState
            {
                Mode = mode,
                HostId = host.Id,
                Name = host.Name ?? string.Empty,
                HostName = host.HostName ?? string.Empty,
                PortText = host.Port.ToString(),
                Username = host.Username ?? string.Empty,
                AuthType = host.AuthType,
                GroupId = host.GroupId ?? string.Empty,
                KeepaliveText = host.Keepalive.ToString(),
                HostFingerprint = host.HostFingerprint ?? string.Empty,
                AppearanceId = host.AppearanceId ?? string.Empty,
                TermType = string.IsNullOrEmpty(host.TermType) ? "xterm-256color" : host.TermType,
                BackspaceSendsCtrlH = host.BackspaceSendsCtrlH,
                InitCommandsText = host.InitCommands == null ? string.Empty : string.Join("\n", host.InitCommands.ToArray()),
                EnvVars = env,
                TmuxAutoAttach = host.TmuxAutoAttach,
                TmuxSessionName = host.TmuxSessionName ?? string.Empty,
                JumpHostId = host.JumpHostId ?? string.Empty,
                KeyId = host.KeyId,
                SortOrder = host.SortOrder,
                LastConnectedAt = host.LastConnectedAt
            };
        }

        private bool SameAs(Host other)
        {
            Host current = ToHost();
            return string.Equals(current.Name, other.Name, StringComparison.Ordinal)
                && string.Equals(current.HostName, other.HostName, StringComparison.Ordinal)
                && current.Port == other.Port
                && string.Equals(current.Username, other.Username, StringComparison.Ordinal)
                && current.AuthType == other.AuthType
                && string.Equals(current.GroupId ?? string.Empty, other.GroupId ?? string.Empty, StringComparison.Ordinal)
                && current.Keepalive == other.Keepalive
                && string.Equals(current.HostFingerprint ?? string.Empty, other.HostFingerprint ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(current.AppearanceId ?? string.Empty, other.AppearanceId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(current.TermType ?? string.Empty, other.TermType ?? string.Empty, StringComparison.Ordinal)
                && current.BackspaceSendsCtrlH == other.BackspaceSendsCtrlH
                && ListEquals(current.InitCommands, other.InitCommands)
                && DictEquals(current.EnvVars, other.EnvVars)
                && current.TmuxAutoAttach == other.TmuxAutoAttach
                && string.Equals(current.TmuxSessionName ?? string.Empty, other.TmuxSessionName ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(current.JumpHostId ?? string.Empty, other.JumpHostId ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool ListEquals(List<string> a, List<string> b)
        {
            if (a == null && b == null)
            {
                return true;
            }
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a == null && b == null)
            {
                return true;
            }
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }
            foreach (var pair in a)
            {
                string value;
                if (!b.TryGetValue(pair.Key, out value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static List<string> ParseInitCommands(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0)
                {
                    result.Add(line);
                }
            }
            return result;
        }

        private bool TryParseEnv(out string error)
        {
            Dictionary<string, string> env;
            return TryBuildEnv(out env, out error);
        }

        private bool TryBuildEnv(out Dictionary<string, string> env, out string error)
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            error = null;
            if (EnvVars == null)
            {
                return true;
            }
            for (int i = 0; i < EnvVars.Count; i++)
            {
                EnvVarRow row = EnvVars[i];
                if (row == null)
                {
                    continue;
                }
                string key = row.Key == null ? string.Empty : row.Key.Trim();
                if (key.Length == 0 && string.IsNullOrEmpty(row.Value))
                {
                    continue;
                }
                if (!IsEnvKey(key))
                {
                    error = ValidationKeys.EnvVarsFormat;
                    return false;
                }
                if (env.ContainsKey(key))
                {
                    error = ValidationKeys.EnvVarsFormat;
                    return false;
                }
                env[key] = row.Value ?? string.Empty;
            }
            return true;
        }

        private static bool IsEnvKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }
            char first = key[0];
            if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
            {
                return false;
            }
            for (int i = 1; i < key.Length; i++)
            {
                char c = key[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryParsePort(string text)
        {
            int ignored;
            return TryParsePort(text, out ignored);
        }

        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            int value;
            if (!TryParseNonNegativeInt(text, out value) || value < 1 || value > 65535)
            {
                return false;
            }
            port = value;
            return true;
        }

        private static bool TryParseKeepalive(string text)
        {
            int ignored;
            return TryParseKeepalive(text, out ignored);
        }

        private static bool TryParseKeepalive(string text, out int value)
        {
            value = 0;
            int parsed;
            if (!TryParseNonNegativeInt(text, out parsed) || parsed > 3600)
            {
                return false;
            }
            value = parsed;
            return true;
        }

        private static bool TryParseNonNegativeInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string trimmed = text.Trim();
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] < '0' || trimmed[i] > '9')
                {
                    return false;
                }
            }
            int n = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                n = (n * 10) + (trimmed[i] - '0');
            }
            value = n;
            return true;
        }

        private static string EmptyToNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
