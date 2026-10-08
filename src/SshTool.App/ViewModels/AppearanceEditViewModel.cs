using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Validation;

namespace SshTool.App.ViewModels
{
    // A03：外观编辑（02-UI-DESIGN.md §5.12）。草稿是仓库对象的 Clone，
    // 未保存离开即丢弃（= 还原）；每次改动由页面同步重建预览（≤100 ms）。
    // 内置主题只读：打开即自动复制为新主题（新 id、BuiltIn=false）。
    public sealed class AppearanceEditViewModel : ViewModelBase
    {
        public const int MinFontSize = 8;
        public const int MaxFontSize = 28;
        public const double MinLineHeight = 1.0;
        public const double MaxLineHeight = 1.6;
        public const int MinPadding = 0;
        public const int MaxPadding = 16;

        private readonly AppServices _services;
        private AppearanceProfile _draft;
        private AppearanceProfile _baseline;
        private bool _isNew;
        private string _title = Localized.Get("AppearanceEdit_TitleNew", "New appearance");
        private Dictionary<string, string> _errors = new Dictionary<string, string>();

        public AppearanceEditViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
        }

        public AppearanceProfile Draft
        {
            get { return _draft; }
        }

        public string Title
        {
            get { return _title; }
            private set
            {
                _title = value;
                RaisePropertyChanged("Title");
            }
        }

        public bool IsBuiltInCopy { get; private set; }

        public bool IsDirty
        {
            get { return _draft != null && _baseline != null && !AppearanceComparer.AreEqual(_draft, _baseline); }
        }

        public IReadOnlyDictionary<string, string> Errors
        {
            get { return _errors; }
        }

        public async Task LoadAsync(string appearanceId)
        {
            IReadOnlyList<AppearanceProfile> all = await _services.AppearanceService.ListAsync().ConfigureAwait(true);
            AppearanceProfile found = null;
            if (!string.IsNullOrEmpty(appearanceId))
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (string.Equals(all[i].Id, appearanceId, StringComparison.Ordinal))
                    {
                        found = all[i];
                        break;
                    }
                }
            }
            if (found == null)
            {
                _draft = BaseForNew(all);
                _isNew = true;
                IsBuiltInCopy = false;
                Title = Localized.Get("AppearanceEdit_TitleNew", "New appearance");
            }
            else if (found.BuiltIn || _services.AppearanceService.IsBuiltIn(found.Id))
            {
                _draft = found.Clone();
                _draft.Id = IdGenerator.NewId();
                _draft.BuiltIn = false;
                _draft.Name = CopyName(found.Name);
                _isNew = true;
                IsBuiltInCopy = true;
                Title = Localized.Get("AppearanceEdit_TitleBuiltInCopy", "New appearance (copy of built-in theme)");
            }
            else
            {
                _draft = found.Clone();
                _isNew = false;
                IsBuiltInCopy = false;
                Title = Localized.Get("AppearanceEdit_TitleEdit", "Edit appearance");
            }
            _baseline = _draft.Clone();
            _errors = new Dictionary<string, string>();
            RaisePropertyChanged("Draft");
            RaisePropertyChanged("IsDirty");
        }

        // 新建以外观最接近的深色内置为底（AMOLED 默认深色，见 §2.1）。
        private static AppearanceProfile BaseForNew(IReadOnlyList<AppearanceProfile> all)
        {
            AppearanceProfile baseProfile = null;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Id, AppearanceResolver.DefaultBuiltInId, StringComparison.Ordinal))
                {
                    baseProfile = all[i];
                    break;
                }
            }
            AppearanceProfile draft = baseProfile != null ? baseProfile.Clone() : Defaults.DefaultAppearance();
            draft.Id = IdGenerator.NewId();
            draft.BuiltIn = false;
            draft.Name = Localized.Get("AppearanceEdit_NewName", "New theme");
            return draft;
        }

        // 「复制为新主题」的默认名：「{原名} 副本」，原名缺失时用「主题」。
        public static string CopyName(string name)
        {
            return Localized.Format("Appearance_CopyName", "{0} copy",
                name ?? Localized.Get("Appearance_DefaultName", "Theme"));
        }

        public void Touch()
        {
            RaisePropertyChanged("IsDirty");
        }

        public void ResetToBaseline()
        {
            if (_draft == null || _baseline == null)
            {
                return;
            }
            _draft = _baseline.Clone();
            _errors = new Dictionary<string, string>();
            RaisePropertyChanged("Draft");
            RaisePropertyChanged("IsDirty");
        }

        public bool Validate()
        {
            var errors = new Dictionary<string, string>();
            if (_draft == null)
            {
                errors["name"] = Localized.Get("AppearanceEdit_ErrNotFound", "Appearance not found");
            }
            else
            {
                if (string.IsNullOrEmpty(_draft.Name) || _draft.Name.Length > 255)
                {
                    errors["name"] = Localized.Get("AppearanceEdit_ErrNameLength", "Name must be 1–255 characters");
                }
                if (_draft.FontSize < MinFontSize || _draft.FontSize > MaxFontSize)
                {
                    errors["fontSize"] = Localized.Get("AppearanceEdit_ErrFontSize", "Font size must be 8–28");
                }
                if (_draft.LineHeight < MinLineHeight || _draft.LineHeight > MaxLineHeight)
                {
                    errors["lineHeight"] = Localized.Get("AppearanceEdit_ErrLineHeight", "Line height must be 1.0–1.6");
                }
                if (_draft.Padding < MinPadding || _draft.Padding > MaxPadding)
                {
                    errors["padding"] = Localized.Get("AppearanceEdit_ErrPadding", "Padding must be 0–16");
                }
                CheckColor(errors, "foreground", _draft.Foreground);
                CheckColor(errors, "background", _draft.Background);
                CheckColor(errors, "cursor", _draft.Cursor);
                CheckColor(errors, "selection", _draft.Selection);
                if (_draft.Palette == null || _draft.Palette.Count != 16)
                {
                    errors["palette"] = Localized.Get("AppearanceEdit_ErrPaletteCount", "Palette must have 16 colors");
                }
                else
                {
                    for (int i = 0; i < 16; i++)
                    {
                        if (!GroupValidator.IsValidColor(_draft.Palette[i]))
                        {
                            errors["palette"] = Localized.Format("AppearanceEdit_ErrPaletteColor", "Palette color {0} is invalid", i + 1);
                            break;
                        }
                    }
                }
            }
            _errors = errors;
            return errors.Count == 0;
        }

        public async Task<bool> SaveAsync()
        {
            if (!Validate())
            {
                return false;
            }
            try
            {
                if (_isNew)
                {
                    await _services.AppearanceService.AddAsync(_draft).ConfigureAwait(true);
                    _isNew = false;
                }
                else
                {
                    await _services.AppearanceService.UpdateAsync(_draft).ConfigureAwait(true);
                }
                _baseline = _draft.Clone();
                RaisePropertyChanged("IsDirty");
                return true;
            }
            catch (Exception ex)
            {
                _errors = new Dictionary<string, string> { { "save", ex.Message } };
                Logger.Log(LogLevel.Error, "AppearanceEdit", ex.GetType().Name);
                return false;
            }
        }

        // 「复制为新主题」：当前草稿另存一份（先校验），页面切到新副本继续编。
        public async Task<string> DuplicateCurrentAsync()
        {
            if (!Validate() || _draft == null)
            {
                return null;
            }
            try
            {
                AppearanceProfile copy = _draft.Clone();
                copy.Id = IdGenerator.NewId();
                copy.BuiltIn = false;
                copy.Name = CopyName(_draft.Name);
                await _services.AppearanceService.AddAsync(copy).ConfigureAwait(true);
                return copy.Id;
            }
            catch (Exception ex)
            {
                _errors = new Dictionary<string, string> { { "save", ex.Message } };
                Logger.Log(LogLevel.Error, "AppearanceEdit", ex.GetType().Name);
                return null;
            }
        }

        private static void CheckColor(Dictionary<string, string> errors, string field, string value)
        {
            if (!GroupValidator.IsValidColor(value))
            {
                errors[field] = Localized.Get("AppearanceEdit_ErrColor", "Color must be #RRGGBB");
            }
        }
    }
}
