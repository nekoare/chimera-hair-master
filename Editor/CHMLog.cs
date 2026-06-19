using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// 高頻度デバッグログのガード。
    /// プレビューのキャッシュヒット/ミス等はパラメータ操作中に連続発火して
    /// コンソールを埋めるため、既定では出力しない。
    /// EditorPrefs "CHM_VerboseLog" を true にすると出力される
    /// </summary>
    internal static class CHMLog
    {
        private const string PrefKey = "CHM_VerboseLog";
        private static bool? _enabled;

        public static bool Enabled
        {
            get
            {
                _enabled ??= EditorPrefs.GetBool(PrefKey, false);
                return _enabled.Value;
            }
            set
            {
                _enabled = value;
                EditorPrefs.SetBool(PrefKey, value);
            }
        }

        public static void Verbose(string message)
        {
            if (Enabled) Debug.Log(message);
        }
    }
}
