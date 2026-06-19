using System;
using System.Collections.Generic;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// targetRenderers のインデックスで対象を参照するエントリ列を、
    /// Renderer の削除に追従させるための純粋なヘルパー。
    /// </summary>
    internal static class RendererIndexRemap
    {
        /// <summary>
        /// targetRenderers から removedIndex を削除した際に、rendererIndex を持つ
        /// エントリ列を詰める。removedIndex を指すエントリは除去し、それより後ろの
        /// rendererIndex は 1 つ前へずらす。
        /// （これを行わないと、削除位置より後ろの Renderer に設定がズレて適用され、
        /// 末尾の設定が失われる）
        /// </summary>
        public static void RemoveRenderer<T>(
            List<T> list,
            int removedIndex,
            Func<T, int> getIndex,
            Action<T, int> setIndex)
        {
            if (list == null) return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                if (item == null) continue;

                int idx = getIndex(item);
                if (idx == removedIndex)
                {
                    list.RemoveAt(i);
                }
                else if (idx > removedIndex)
                {
                    setIndex(item, idx - 1);
                }
            }
        }
    }
}
