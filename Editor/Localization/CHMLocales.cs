using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Localization
{
    internal static class CHMLocales
    {
        private static readonly string[] PoGuids =
        {
            "25abf5ff01bf4c98a6a9b0dc46782d61", // ja-JP.po
            "c9b5eaaad1a649c39b1af196527dcfd2", // en-US.po
            "6c289ad21d0d41d6b3d330d087f26186", // zh-Hans.po
            "18d193ccb2c744378068007aa12cf3c8", // ko-KR.po
        };

        public static readonly Localizer L = new Localizer(
            "ja-JP",
            () => PoGuids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationAsset>)
                .Where(a => a != null)
                .ToList()
        );

        public static string Tr(string key) => L.GetLocalizedString(key);

        private static readonly (string code, string display)[] Languages =
        {
            ("ja-jp", "日本語"),
            ("en-us", "English"),
            ("zh-hans", "简体中文"),
            ("ko-kr", "한국어"),
        };

        /// <summary>
        /// ヘッダー右寄せで言語切替ドロップダウンを描画する。
        /// NDMF の LanguagePrefs を直接操作するため、他の NDMF 連携ツールとも同期する。
        /// </summary>
        public static void DrawLanguagePicker()
        {
            var current = LanguagePrefs.Language;
            int selected = 0;
            for (int i = 0; i < Languages.Length; i++)
            {
                if (Languages[i].code == current) { selected = i; break; }
            }

            var displays = new string[Languages.Length];
            for (int i = 0; i < Languages.Length; i++) displays[i] = Languages[i].display;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("Language:", GUILayout.ExpandWidth(false));
                int newSelected = EditorGUILayout.Popup(selected, displays, GUILayout.Width(100));
                if (newSelected != selected)
                {
                    LanguagePrefs.Language = Languages[newSelected].code;
                }
            }
        }
    }
}
