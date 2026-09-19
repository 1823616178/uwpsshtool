using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Storage;

namespace SshTool.App.ViewModels
{
    public sealed class KnownHostRow
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Fingerprint { get; set; }
        public string RandomArt { get; set; }
        public KnownHost Source { get; set; }
    }

    public sealed class KnownHostsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private IReadOnlyList<KnownHost> _all = new KnownHost[0];
        private string _search = string.Empty;
        private KnownHostRow _selected;

        public KnownHostsViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            _services = services;
            Rows = new ObservableCollection<KnownHostRow>();
            var ignore = RefreshAsync();
        }

        public ObservableCollection<KnownHostRow> Rows { get; private set; }

        public string Search
        {
            get { return _search; }
            set
            {
                if (SetProperty(ref _search, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public KnownHostRow Selected
        {
            get { return _selected; }
            set
            {
                SetProperty(ref _selected, value);
                RaisePropertyChanged("HasSelection");
            }
        }

        public bool HasSelection
        {
            get { return _selected != null; }
        }

        public async Task RefreshAsync()
        {
            _all = await _services.KnownHosts.GetAllAsync().ConfigureAwait(true);
            ApplyFilter();
        }

        public async Task DeleteSelectedAsync()
        {
            if (_selected == null)
            {
                return;
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                "删除已知主机？",
                "删除「" + _selected.Title + "」后，下次连接会重新校验主机密钥。",
                "删除", "取消", true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return;
            }
            await _services.KnownHosts.RemoveAsync(_selected.Id, ChangeOrigin.User).ConfigureAwait(true);
            Selected = null;
            await RefreshAsync().ConfigureAwait(true);
        }

        private void ApplyFilter()
        {
            string q = _search.Trim();
            Rows.Clear();
            for (int i = 0; i < _all.Count; i++)
            {
                KnownHost kh = _all[i];
                if (kh == null)
                {
                    continue;
                }
                if (q.Length > 0
                    && !Contains(kh.Host, q)
                    && !Contains(kh.KeyType, q)
                    && !Contains(kh.FingerprintSha256, q))
                {
                    continue;
                }
                Rows.Add(ToRow(kh));
            }
        }

        private static KnownHostRow ToRow(KnownHost kh)
        {
            string art = null;
            if (kh.Extra != null && kh.Extra["randomArt"] != null)
            {
                art = (string)kh.Extra["randomArt"];
            }
            string tail = CredentialDraft.FingerprintTail(kh.FingerprintSha256);
            return new KnownHostRow
            {
                Id = kh.Id,
                Title = (kh.Host ?? string.Empty) + ":" + kh.Port.ToString(),
                Subtitle = (kh.KeyType ?? string.Empty) + " · " + tail + " · " + (kh.AddedAt ?? string.Empty),
                Fingerprint = kh.FingerprintSha256 ?? string.Empty,
                RandomArt = art ?? string.Empty,
                Source = kh
            };
        }

        private static bool Contains(string value, string q)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
