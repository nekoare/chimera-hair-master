using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// targetRenderers の削除・並べ替えに rendererIndex ベースの各設定を追従させる。
    ///
    /// 各設定（materialSelections / rendererBrightnessAdjustments /
    /// rendererBlurSharpAdjustments / islandPlacements / rendererDeformations）は
    /// targetRenderers 内のインデックスで対象を参照しているため、リストの途中削除や
    /// 並べ替えで「1つ後ろの Renderer に設定が適用される」ズレが起きる。
    /// コンポーネントに保持した InstanceID スナップショットとの差分から
    /// 旧index→新index のマッピングを作り、全リストを一括で再マップする。
    /// </summary>
    internal static class RendererIndexSynchronizer
    {
        /// <summary>
        /// スナップショットを現在の構成で初期化する（再マップはしない）。
        /// Inspector の OnEnable 等、変更検知を始める前に呼ぶ
        /// </summary>
        public static void InitializeSnapshot(ChimeraHairMaster component)
        {
            if (component == null || component.targetRenderers == null) return;
            if (component.editorRendererIdsSnapshot != null
                && component.editorRendererIdsSnapshot.Count > 0) return;

            SaveSnapshot(component, BuildCurrentIds(component));
        }

        /// <summary>
        /// targetRenderers の変更を検知し、必要なら各設定の rendererIndex を再マップする。
        /// Inspector の ApplyModifiedProperties 後に毎回呼んでよい（変更が無ければ何もしない）
        /// </summary>
        public static void SyncAfterRendererListChange(ChimeraHairMaster component)
        {
            if (component == null || component.targetRenderers == null) return;

            var current = BuildCurrentIds(component);
            var snapshot = component.editorRendererIdsSnapshot;

            if (snapshot == null || snapshot.Count == 0)
            {
                // 初回はスナップショットの初期化のみ（差分の起点が無いため再マップ不能）
                SaveSnapshot(component, current);
                return;
            }

            if (SequenceEqual(snapshot, current)) return;

            // 旧index → 新index のマッピング（InstanceID の一致で対応付け、重複IDは先頭一致）
            var map = new Dictionary<int, int>();
            var used = new bool[current.Count];
            for (int oldIdx = 0; oldIdx < snapshot.Count; oldIdx++)
            {
                int id = snapshot[oldIdx];
                if (id == 0) continue;
                for (int newIdx = 0; newIdx < current.Count; newIdx++)
                {
                    if (used[newIdx] || current[newIdx] != id) continue;
                    map[oldIdx] = newIdx;
                    used[newIdx] = true;
                    break;
                }
            }

            // 共通の Renderer がひとつも無い場合（Unity 再起動による InstanceID 変化や
            // 全入れ替え）は、再マップの根拠が無いためスナップショットだけ更新する
            if (map.Count == 0)
            {
                SaveSnapshot(component, current);
                return;
            }

            Undo.RecordObject(component, "Sync Renderer Indices");

            RemapIndexedList(component.materialSelections, map,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            RemapIndexedList(component.rendererBrightnessAdjustments, map,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            RemapIndexedList(component.rendererBlurSharpAdjustments, map,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            RemapIndexedList(component.islandPlacements, map,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            RemapIndexedList(component.rendererDeformations, map,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);
            RemapIndexedList(component.colorMasks, map,
                e => e.rendererIndex, (e, i) => e.rendererIndex = i);

            // 単一 index 参照（参照先 Renderer が削除されたら無効値 -1 に落とす）
            if (component.strandPatternSettings != null)
            {
                component.strandPatternSettings.referenceRendererIndex =
                    RemapSingleIndex(component.strandPatternSettings.referenceRendererIndex, map);
            }
            component.deformEditingRendererIndex =
                RemapSingleIndex(component.deformEditingRendererIndex, map);

            RemapPositionalUvPlacements(component, map, current.Count);

            SaveSnapshot(component, current);
            EditorUtility.SetDirty(component);
        }

        private static List<int> BuildCurrentIds(ChimeraHairMaster component)
        {
            var ids = new List<int>(component.targetRenderers.Count);
            foreach (var r in component.targetRenderers)
            {
                ids.Add(r != null ? r.GetInstanceID() : 0);
            }
            return ids;
        }

        private static void SaveSnapshot(ChimeraHairMaster component, List<int> ids)
        {
            component.editorRendererIdsSnapshot ??= new List<int>();
            component.editorRendererIdsSnapshot.Clear();
            component.editorRendererIdsSnapshot.AddRange(ids);
        }

        private static bool SequenceEqual(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// 単一の rendererIndex を再マップする。
        /// 参照先 Renderer が削除された場合は -1（無効）を返す。
        /// 元から負値（未設定）の場合はそのまま返す。
        /// </summary>
        private static int RemapSingleIndex(int oldIndex, Dictionary<int, int> map)
        {
            if (oldIndex < 0) return oldIndex;
            return map.TryGetValue(oldIndex, out int newIndex) ? newIndex : -1;
        }

        /// <summary>
        /// rendererIndex フィールドを持つエントリのリストを再マップする。
        /// 対応する Renderer が削除されたエントリはリストから除去する
        /// </summary>
        private static void RemapIndexedList<T>(
            List<T> list,
            Dictionary<int, int> map,
            System.Func<T, int> getIndex,
            System.Action<T, int> setIndex) where T : class
        {
            if (list == null) return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                if (item == null) continue;

                int oldIdx = getIndex(item);
                if (oldIdx < 0) continue;

                if (map.TryGetValue(oldIdx, out int newIdx))
                {
                    setIndex(item, newIdx);
                }
                else
                {
                    // 対応する Renderer がリストから消えた → エントリも破棄
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// uvPlacements は targetRenderers と位置対応のリストのため、並びを再構築する
        /// </summary>
        private static void RemapPositionalUvPlacements(
            ChimeraHairMaster component,
            Dictionary<int, int> map,
            int newCount)
        {
            if (component.uvPlacements == null) return;

            var old = component.uvPlacements;
            var rebuilt = new List<UVIslandPlacement>(newCount);
            for (int i = 0; i < newCount; i++)
            {
                rebuilt.Add(null);
            }

            foreach (var kvp in map)
            {
                if (kvp.Key < old.Count && kvp.Value < newCount)
                {
                    rebuilt[kvp.Value] = old[kvp.Key];
                }
            }

            // 新規 Renderer のスロットは既定値で埋める（AddRenderer と同じ扱い）
            for (int i = 0; i < newCount; i++)
            {
                if (rebuilt[i] == null)
                {
                    rebuilt[i] = new UVIslandPlacement(component.targetRenderers[i]);
                }
            }

            component.uvPlacements.Clear();
            component.uvPlacements.AddRange(rebuilt);
        }
    }
}
