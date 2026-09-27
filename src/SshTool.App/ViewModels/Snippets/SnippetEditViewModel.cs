using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Terminal;
using SshTool.Core.Storage;
using SshTool.Core.Validation;

namespace SshTool.App.ViewModels.Snippets
{
    // U13：片段编辑页 ViewModel。新增 / 编辑同一页（见 SnippetEditArgs）。
    public sealed class SnippetEditViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private Snippet _editing;
        private bool _isNew;

        private string _name = string.Empty;
        private string _groupName = string.Empty;
        private string _content = string.Empty;
        private bool _sendEnter = true;
        private Dictionary<string, string> _errors = new Dictionary<string, string>();
        private bool _lastSaveHadErrors;

        public SnippetEditViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
            ExistingGroups = new List<string>();
        }

        public string Title
        {
                get { return _isNew
                    ? Localized.Get("Snippet_EditNew", "新建片段")
                    : Localized.Get("Snippet_EditExisting", "编辑片段"); }
        }

        public bool IsNew
        {
            get { return _isNew; }
        }

        public string Name
        {
            get { return _name; }
            set
            {
                if (SetProperty(ref _name, value ?? string.Empty))
                {
                    RaisePropertyChanged("IsDirty");
                }
            }
        }

        public string GroupName
        {
            get { return _groupName; }
            set
            {
                if (SetProperty(ref _groupName, value ?? string.Empty))
                {
                    RaisePropertyChanged("IsDirty");
                }
            }
        }

        public string Content
        {
            get { return _content; }
            set
            {
                if (SetProperty(ref _content, value ?? string.Empty))
                {
                    RaisePropertyChanged("IsDirty");
                    RaisePropertyChanged("VariableHint");
                }
            }
        }

        public bool SendEnter
        {
            get { return _sendEnter; }
            set
            {
                if (SetProperty(ref _sendEnter, value))
                {
                    RaisePropertyChanged("IsDirty");
                }
            }
        }

        // 已有分组名（分组输入框的联想建议用）。
        public List<string> ExistingGroups { get; private set; }

        public IReadOnlyDictionary<string, string> Errors
        {
            get { return _errors; }
        }

        public bool LastSaveHadErrors
        {
            get { return _lastSaveHadErrors; }
        }

        public bool IsDirty
        {
            get
            {
                if (_editing == null)
                {
                    return _name.Length > 0 || _groupName.Length > 0 || _content.Length > 0;
                }
                return !string.Equals(_name, _editing.Name ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(_groupName ?? string.Empty, _editing.GroupName ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(_content, _editing.Content ?? string.Empty, StringComparison.Ordinal)
                    || _sendEnter != _editing.SendEnter;
            }
        }

        // 编辑页变量说明行：列出模板里的待填变量名。
        public string VariableHint
        {
            get
            {
                IReadOnlyList<string> vars = SnippetTemplate.CollectVariables(_content);
                if (vars.Count == 0)
                {
                    return Localized.Get("Snippet_VarHint",
                        "可用 ${host} ${user} ${port} ${name}，其他 ${xxx} 发送时填写");
                }
                return Localized.Format("Snippet_VarsPending", "待填变量：{0}",
                    string.Join(Localized.Get("Snippet_VarJoiner", "、"), ListOf(vars)));
            }
        }

        public async Task LoadAsync(string snippetId)
        {
            IReadOnlyList<Snippet> all = await _services.Snippets.GetAllAsync().ConfigureAwait(true);
            ExistingGroups = SnippetGrouping.ExistingGroups(all);
            RaisePropertyChanged("ExistingGroups");
            if (string.IsNullOrEmpty(snippetId))
            {
                _isNew = true;
                _editing = null;
                _name = string.Empty;
                _groupName = string.Empty;
                _content = string.Empty;
                _sendEnter = true;
            }
            else
            {
                Snippet found = await _services.Snippets.GetByIdAsync(snippetId).ConfigureAwait(true);
                if (found == null)
                {
                    _isNew = true;
                    _editing = null;
                    _name = string.Empty;
                    _groupName = string.Empty;
                    _content = string.Empty;
                    _sendEnter = true;
                }
                else
                {
                    _isNew = false;
                    _editing = found.Clone();
                    _name = _editing.Name ?? string.Empty;
                    _groupName = _editing.GroupName ?? string.Empty;
                    _content = _editing.Content ?? string.Empty;
                    _sendEnter = _editing.SendEnter;
                }
            }
            _errors = new Dictionary<string, string>();
            _lastSaveHadErrors = false;
            RaisePropertyChanged("Title");
            RaisePropertyChanged("Name");
            RaisePropertyChanged("GroupName");
            RaisePropertyChanged("Content");
            RaisePropertyChanged("SendEnter");
            RaisePropertyChanged("VariableHint");
            RaisePropertyChanged("IsDirty");
        }

        // 校验通过并落盘返回 true；失败时 Errors 更新、返回 false（页面负责聚焦）。
        public async Task<bool> SaveAsync()
        {
            var draft = new Snippet
            {
                Id = _editing != null ? _editing.Id : IdGenerator.NewId(),
                Name = _name.Trim(),
                Content = _content,
                GroupName = _groupName == null ? string.Empty : _groupName.Trim(),
                SortOrder = _editing != null ? _editing.SortOrder : NextOrder(),
                SendEnter = _sendEnter
            };
            ValidationResult v = SnippetValidator.Validate(draft);
            var errors = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> pair in v.Errors)
            {
                errors[pair.Key] = pair.Value;
            }
            _errors = errors;
            RaisePropertyChanged("Errors");
            if (!v.IsValid)
            {
                _lastSaveHadErrors = true;
                return false;
            }
            _lastSaveHadErrors = false;
            if (_isNew || _editing == null)
            {
                await _services.Snippets.AddAsync(draft, ChangeOrigin.User).ConfigureAwait(true);
            }
            else
            {
                await _services.Snippets.UpdateAsync(draft, ChangeOrigin.User).ConfigureAwait(true);
            }
            _editing = draft.Clone();
            _isNew = false;
            RaisePropertyChanged("Title");
            RaisePropertyChanged("IsDirty");
            Logger.Log(LogLevel.Info, "Snippet", "保存片段 name=\"" + draft.Name + "\"");
            Navigation.GoBack();
            return true;
        }

        public string FirstErrorField()
        {
            if (_errors.ContainsKey("name"))
            {
                return "name";
            }
            if (_errors.ContainsKey("content"))
            {
                return "content";
            }
            foreach (KeyValuePair<string, string> pair in _errors)
            {
                return pair.Key;
            }
            return null;
        }

        public async Task<bool> DeleteAsync()
        {
            if (_isNew || _editing == null)
            {
                return false;
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                "删除片段？",
                "删除「" + (_editing.Name ?? string.Empty) + "」后无法恢复。",
                "删除", "取消", true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return false;
            }
            await _services.Snippets.RemoveAsync(_editing.Id, ChangeOrigin.User).ConfigureAwait(true);
            Navigation.GoBack();
            return true;
        }

        private int NextOrder()
        {
            return 0;
        }

        private static string[] ListOf(IReadOnlyList<string> vars)
        {
            var arr = new string[vars.Count];
            for (int i = 0; i < vars.Count; i++)
            {
                arr[i] = "${" + vars[i] + "}";
            }
            return arr;
        }
    }
}
