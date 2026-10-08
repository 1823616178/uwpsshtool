using System.Collections.Generic;
using SshTool.Core.Models;
using SshTool.Core.Storage;

namespace SshTool.Core.Hosts
{
    public enum CredentialFieldState
    {
        Unchanged = 0,
        Modified = 1,
        Cleared = 2
    }

    public sealed class SecretMutation
    {
        public SecretMutation(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; private set; }
        // null = 删除该键
        public string Value { get; private set; }
    }

    // fix/functional-pass（P2-2）：切换将丢弃的内容（文案由 App 查 resw：HostEdit_SwitchAuthDrop*）。
    public enum AuthSwitchDrop
    {
        None = 0,
        Password,
        KeyAndPassphrase
    }

    public sealed class AuthSwitchPreview
    {
        public bool NeedsConfirm { get; set; }
        public AuthSwitchDrop Drop { get; set; }
        // 诊断文本（英文）；面向用户的文案按 Drop 本地化。
        public string Message { get; set; }
        public AuthType From { get; set; }
        public AuthType To { get; set; }
    }

    public sealed class CredentialDraft
    {
        public AuthType AuthType { get; set; }
        public string Password { get; set; }
        public bool RememberPassword { get; set; }
        public bool HasSavedPassword { get; set; }
        public CredentialFieldState PasswordState { get; set; }

        public string KeyId { get; set; }
        public string Passphrase { get; set; }
        public bool RememberPassphrase { get; set; }
        public bool HasSavedPassphrase { get; set; }
        public CredentialFieldState PassphraseState { get; set; }

        public static CredentialDraft ForNew()
        {
            return new CredentialDraft
            {
                AuthType = AuthType.Password,
                Password = string.Empty,
                RememberPassword = true,
                Passphrase = string.Empty,
                RememberPassphrase = true
            };
        }

        public void SetPassword(string value)
        {
            Password = value ?? string.Empty;
            PasswordState = CredentialFieldState.Modified;
        }

        public void SetRememberPassword(bool remember)
        {
            RememberPassword = remember;
            if (!remember && HasSavedPassword && PasswordState == CredentialFieldState.Unchanged)
            {
                PasswordState = CredentialFieldState.Cleared;
            }
            else if (remember && PasswordState == CredentialFieldState.Cleared && string.IsNullOrEmpty(Password))
            {
                PasswordState = CredentialFieldState.Unchanged;
            }
        }

        public void SetPassphrase(string value)
        {
            Passphrase = value ?? string.Empty;
            PassphraseState = CredentialFieldState.Modified;
        }

        public void SetRememberPassphrase(bool remember)
        {
            RememberPassphrase = remember;
            if (!remember && HasSavedPassphrase && PassphraseState == CredentialFieldState.Unchanged)
            {
                PassphraseState = CredentialFieldState.Cleared;
            }
            else if (remember && PassphraseState == CredentialFieldState.Cleared && string.IsNullOrEmpty(Passphrase))
            {
                PassphraseState = CredentialFieldState.Unchanged;
            }
        }

        public AuthSwitchPreview PreviewSwitch(AuthType next)
        {
            var preview = new AuthSwitchPreview
            {
                From = AuthType,
                To = next,
                NeedsConfirm = false,
                Message = string.Empty
            };
            if (AuthType == next)
            {
                return preview;
            }
            bool dropPassword = AuthType == AuthType.Password
                && (HasSavedPassword || !string.IsNullOrEmpty(Password));
            bool dropKey = AuthType == AuthType.Key
                && (!string.IsNullOrEmpty(KeyId) || HasSavedPassphrase || !string.IsNullOrEmpty(Passphrase));
            if (dropPassword || dropKey)
            {
                preview.NeedsConfirm = true;
                preview.Drop = dropPassword ? AuthSwitchDrop.Password : AuthSwitchDrop.KeyAndPassphrase;
                preview.Message = preview.Drop.ToString();
            }
            return preview;
        }

        public void ApplySwitch(AuthType next)
        {
            if (AuthType == AuthType.Password && next != AuthType.Password)
            {
                Password = string.Empty;
                if (HasSavedPassword)
                {
                    PasswordState = CredentialFieldState.Cleared;
                }
                RememberPassword = false;
            }
            if (AuthType == AuthType.Key && next != AuthType.Key)
            {
                KeyId = null;
                Passphrase = string.Empty;
                if (HasSavedPassphrase)
                {
                    PassphraseState = CredentialFieldState.Cleared;
                }
                RememberPassphrase = false;
            }
            AuthType = next;
        }

        public IReadOnlyList<SecretMutation> BuildMutations(string hostId)
        {
            var list = new List<SecretMutation>();
            if (string.IsNullOrEmpty(hostId))
            {
                return list;
            }

            if (AuthType == AuthType.Password)
            {
                AddPasswordMutation(list, hostId);
                if (PassphraseState == CredentialFieldState.Cleared || HasSavedPassphrase)
                {
                    list.Add(new SecretMutation(SecretKeys.HostPassphrase(hostId), null));
                }
            }
            else if (AuthType == AuthType.Key)
            {
                AddPassphraseMutation(list, hostId);
                if (PasswordState == CredentialFieldState.Cleared || HasSavedPassword)
                {
                    list.Add(new SecretMutation(SecretKeys.HostPassword(hostId), null));
                }
            }
            else
            {
                if (PasswordState == CredentialFieldState.Cleared || HasSavedPassword)
                {
                    list.Add(new SecretMutation(SecretKeys.HostPassword(hostId), null));
                }
                if (PassphraseState == CredentialFieldState.Cleared || HasSavedPassphrase)
                {
                    list.Add(new SecretMutation(SecretKeys.HostPassphrase(hostId), null));
                }
            }
            return list;
        }

        public void ClearPlaintext()
        {
            Password = string.Empty;
            Passphrase = string.Empty;
        }

        public static string FingerprintTail(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return string.Empty;
            }
            string s = fingerprint;
            int colon = s.LastIndexOf(':');
            if (colon >= 0 && colon < s.Length - 1)
            {
                s = s.Substring(colon + 1);
            }
            s = s.Replace(":", string.Empty);
            if (s.Length <= 8)
            {
                return s;
            }
            return s.Substring(s.Length - 8);
        }

        private void AddPasswordMutation(List<SecretMutation> list, string hostId)
        {
            string key = SecretKeys.HostPassword(hostId);
            if (PasswordState == CredentialFieldState.Cleared)
            {
                list.Add(new SecretMutation(key, null));
                return;
            }
            if (PasswordState == CredentialFieldState.Modified)
            {
                if (RememberPassword && !string.IsNullOrEmpty(Password))
                {
                    list.Add(new SecretMutation(key, Password));
                }
                else
                {
                    list.Add(new SecretMutation(key, null));
                }
            }
        }

        private void AddPassphraseMutation(List<SecretMutation> list, string hostId)
        {
            string key = SecretKeys.HostPassphrase(hostId);
            if (PassphraseState == CredentialFieldState.Cleared)
            {
                list.Add(new SecretMutation(key, null));
                return;
            }
            if (PassphraseState == CredentialFieldState.Modified)
            {
                if (RememberPassphrase && !string.IsNullOrEmpty(Passphrase))
                {
                    list.Add(new SecretMutation(key, Passphrase));
                }
                else
                {
                    list.Add(new SecretMutation(key, null));
                }
            }
        }
    }
}
