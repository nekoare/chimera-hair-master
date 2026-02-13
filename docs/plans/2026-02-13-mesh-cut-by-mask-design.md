# マスクによるメッシュカット機能 - 実装プラン

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** マスクテクスチャで指定した部分だけを抽出してメッシュ統合できるようにする

**Architecture:** MaterialSelectionEntry にマスクフィールドを追加し、NDMF の Transforming フェーズの先頭に MeshCutPass を挿入する。ビルド時は三角形を実際に除去し、プレビュー時は退化三角形で非表示にする。

**Tech Stack:** Unity C# / NDMF / lilToon

**制約:** GitHub への push 禁止（ユーザーの許可があるまで）

---

### Task 1: データモデル — MaterialSelectionEntry にマスクフィールドを追加

**Files:**
- Modify: `Runtime/MaterialSelectionEntry.cs:26`

**Step 1: meshCutMask フィールドを追加**

`isIncluded` フィールドの直後に追加:

```csharp
/// <summary>
/// メッシュカット用マスクテクスチャ（null = マスクなし、白=残す、黒=削除）
/// </summary>
public Texture2D meshCutMask;
```

**Step 2: コンストラクタの確認**

既存のコンストラクタに meshCutMask 引数を追加する必要はない。
null がデフォルトで「マスクなし」を意味するため、既存コードへの影響なし。

---

### Task 2: メッシュカット処理クラスを作成

**Files:**
- Create: `Editor/Processing/MeshCutter.cs`

**Step 1: MeshCutter クラスを実装**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor.Processing
{
    /// <summary>
    /// マスクテクスチャに基づいてメッシュの三角形を除去する
    /// </summary>
    public static class MeshCutter
    {
        private const float Threshold = 0.5f;

        /// <summary>
        /// マスクに基づいてメッシュの指定サブメッシュから三角形を除去する
        /// </summary>
        /// <param name="mesh">対象メッシュ（複製済みであること）</param>
        /// <param name="submeshIndex">対象サブメッシュのインデックス</param>
        /// <param name="mask">マスクテクスチャ（白=残す、黒=削除）</param>
        /// <returns>除去された三角形の数</returns>
        public static int CutMeshByMask(Mesh mesh, int submeshIndex, Texture2D mask)
        {
            if (mesh == null || mask == null) return 0;
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return 0;

            var readableMask = GetReadableTexture(mask);
            var uv = mesh.uv; // UV0
            var triangles = mesh.GetTriangles(submeshIndex);

            var newTriangles = new List<int>();
            int removedCount = 0;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];

                // 3頂点のうち1つでも黒なら三角形を削除
                if (IsVertexMasked(uv[v0], readableMask) ||
                    IsVertexMasked(uv[v1], readableMask) ||
                    IsVertexMasked(uv[v2], readableMask))
                {
                    removedCount++;
                    continue;
                }

                newTriangles.Add(v0);
                newTriangles.Add(v1);
                newTriangles.Add(v2);
            }

            mesh.SetTriangles(newTriangles, submeshIndex);

            if (readableMask != mask)
                Object.DestroyImmediate(readableMask);

            return removedCount;
        }

        /// <summary>
        /// 退化三角形方式でマスクカットのプレビューを適用する
        /// 三角形を削除せず、3頂点を同じ頂点に置き換えて非表示にする
        /// </summary>
        public static int ApplyPreviewCut(Mesh mesh, int submeshIndex, Texture2D mask)
        {
            if (mesh == null || mask == null) return 0;
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return 0;

            var readableMask = GetReadableTexture(mask);
            var uv = mesh.uv;
            var triangles = mesh.GetTriangles(submeshIndex);
            int removedCount = 0;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];

                if (IsVertexMasked(uv[v0], readableMask) ||
                    IsVertexMasked(uv[v1], readableMask) ||
                    IsVertexMasked(uv[v2], readableMask))
                {
                    // 退化三角形: 3頂点を同じ頂点にして面積ゼロにする
                    triangles[i + 1] = v0;
                    triangles[i + 2] = v0;
                    removedCount++;
                }
            }

            mesh.SetTriangles(triangles, submeshIndex);

            if (readableMask != mask)
                Object.DestroyImmediate(readableMask);

            return removedCount;
        }

        /// <summary>
        /// 頂点がマスクにより削除対象かどうかを判定する
        /// </summary>
        private static bool IsVertexMasked(Vector2 uv, Texture2D mask)
        {
            // UV を 0-1 にラップ
            float u = Mathf.Repeat(uv.x, 1f);
            float v = Mathf.Repeat(uv.y, 1f);

            int x = Mathf.Clamp((int)(u * mask.width), 0, mask.width - 1);
            int y = Mathf.Clamp((int)(v * mask.height), 0, mask.height - 1);

            Color pixel = mask.GetPixel(x, y);
            float grayscale = pixel.grayscale;

            // 黒（< 0.5）= 削除対象
            return grayscale < Threshold;
        }

        /// <summary>
        /// テクスチャが読み取り不可の場合、RenderTexture 経由で読み取り可能なコピーを作成する
        /// </summary>
        private static Texture2D GetReadableTexture(Texture2D source)
        {
            if (source.isReadable) return source;

            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);

            var previous = RenderTexture.active;
            RenderTexture.active = rt;

            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            return readable;
        }
    }
}
```

---

### Task 3: MeshCutPass を作成

**Files:**
- Create: `Editor/NDMF/MeshCutPass.cs`

**Step 1: MeshCutPass クラスを実装**

既存の MeshMergePass.cs (L11-13) を参考にしたパス構造:

```csharp
using System.Collections.Generic;
using System.Linq;
using ChimeraHairMaster.Editor.Processing;
using nadena.dev.ndmf;
using UnityEngine;

namespace ChimeraHairMaster.Editor.NDMF
{
    /// <summary>
    /// マスクに基づくメッシュカット処理パス
    /// メッシュ統合前に実行し、不要な三角形を除去する
    /// </summary>
    public class MeshCutPass : Pass<MeshCutPass>
    {
        public override string DisplayName => "CHM: Mesh Cut";

        protected override void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<ChimeraHairMaster>(true);

            foreach (var component in components)
            {
                if (!component.isEnabled) continue;
                if (!component.enableMeshMerge) continue;

                ProcessMeshCut(component);
            }
        }

        private void ProcessMeshCut(ChimeraHairMaster component)
        {
            // マスクが設定されているエントリを Renderer ごとにグループ化
            var masksByRenderer = new Dictionary<int, List<(int submeshIndex, Texture2D mask)>>();

            foreach (var entry in component.materialSelections)
            {
                if (!entry.isIncluded) continue;
                if (entry.meshCutMask == null) continue;

                if (!masksByRenderer.ContainsKey(entry.rendererIndex))
                    masksByRenderer[entry.rendererIndex] = new List<(int, Texture2D)>();
                masksByRenderer[entry.rendererIndex].Add((entry.submeshIndex, entry.meshCutMask));
            }

            if (masksByRenderer.Count == 0) return;

            Debug.Log($"[ChimeraHairMaster] メッシュカット処理開始: {component.gameObject.name}");

            foreach (var kvp in masksByRenderer)
            {
                int rendererIndex = kvp.Key;
                var submeshMasks = kvp.Value;

                if (rendererIndex < 0 || rendererIndex >= component.targetRenderers.Count) continue;
                var renderer = component.targetRenderers[rendererIndex];
                if (renderer == null || renderer.sharedMesh == null) continue;

                // メッシュを複製（元メッシュを壊さないため）
                var meshCopy = Object.Instantiate(renderer.sharedMesh);
                meshCopy.name = renderer.sharedMesh.name + "_Cut";
                int totalRemoved = 0;

                foreach (var (submeshIndex, mask) in submeshMasks)
                {
                    int removed = MeshCutter.CutMeshByMask(meshCopy, submeshIndex, mask);
                    totalRemoved += removed;
                    Debug.Log($"[ChimeraHairMaster] メッシュカット: {renderer.name} SubMesh {submeshIndex} から {removed} 三角形を除去");
                }

                renderer.sharedMesh = meshCopy;
                Debug.Log($"[ChimeraHairMaster] メッシュカット完了: {renderer.name} (合計 {totalRemoved} 三角形除去)");
            }
        }
    }
}
```

---

### Task 4: プラグインに MeshCutPass を登録

**Files:**
- Modify: `Editor/NDMF/ChimeraHairMasterPlugin.cs:19-24`

**Step 1: MeshCutPass をチェーンの先頭に追加**

変更前 (L19-24):
```csharp
InPhase(BuildPhase.Transforming)
    .BeforePlugin("nadena.dev.modular-avatar")
    .Run(ColorTransformPass.Instance)
    .PreviewingWith(new ChimeraHairMasterPreview())
    .Then.Run(TextureAtlasPass.Instance)
    .Then.Run(MeshMergePass.Instance);
```

変更後:
```csharp
InPhase(BuildPhase.Transforming)
    .BeforePlugin("nadena.dev.modular-avatar")
    .Run(MeshCutPass.Instance)
    .Then.Run(ColorTransformPass.Instance)
    .PreviewingWith(new ChimeraHairMasterPreview())
    .Then.Run(TextureAtlasPass.Instance)
    .Then.Run(MeshMergePass.Instance);
```

---

### Task 5: Window UI にマスク設定を追加

**Files:**
- Modify: `Editor/Window/ChimeraHairMasterWindow.cs:440-462`

**Step 1: サブメッシュ行にマスクフィールドを追加**

変更前 (L440-462):
```csharp
for (int s = 0; s < submeshCount; s++)
{
    EditorGUILayout.BeginHorizontal();

    bool currentValue = GetMaterialIncluded(r, s);
    bool newValue = GUILayout.Toggle(currentValue, "", GUILayout.Width(20));

    if (newValue != currentValue)
    {
        SetMaterialIncluded(r, s, newValue);
        UpdateIslandPlacements();
        GUI.changed = true;
    }

    string matName = s < materials.Length && materials[s] != null
        ? materials[s].name
        : $"(Material {s})";
    EditorGUILayout.LabelField($"SubMesh {s}: {matName}");

    EditorGUILayout.EndHorizontal();
}
```

変更後:
```csharp
for (int s = 0; s < submeshCount; s++)
{
    EditorGUILayout.BeginHorizontal();

    bool currentValue = GetMaterialIncluded(r, s);
    bool newValue = GUILayout.Toggle(currentValue, "", GUILayout.Width(20));

    if (newValue != currentValue)
    {
        SetMaterialIncluded(r, s, newValue);
        UpdateIslandPlacements();
        GUI.changed = true;
    }

    string matName = s < materials.Length && materials[s] != null
        ? materials[s].name
        : $"(Material {s})";
    EditorGUILayout.LabelField($"SubMesh {s}: {matName}");

    EditorGUILayout.EndHorizontal();

    // マスクカット設定（統合対象 かつ メッシュ統合有効時のみ表示）
    if (currentValue && enableMeshMerge)
    {
        EditorGUI.indentLevel++;
        EditorGUILayout.BeginHorizontal();

        var currentMask = GetMeshCutMask(r, s);
        var newMask = (Texture2D)EditorGUILayout.ObjectField(
            "カットマスク", currentMask, typeof(Texture2D), false);

        if (newMask != currentMask)
        {
            SetMeshCutMask(r, s, newMask);
            GUI.changed = true;
        }

        EditorGUILayout.EndHorizontal();
        EditorGUI.indentLevel--;
    }
}
```

**Step 2: GetMeshCutMask / SetMeshCutMask ヘルパーを追加**

`SetMaterialIncluded` メソッド (L2248-2261) の後に追加:

```csharp
/// <summary>
/// 指定したRenderer/Submeshのメッシュカットマスクを取得
/// </summary>
private Texture2D GetMeshCutMask(int rendererIndex, int submeshIndex)
{
    var entry = materialSelections.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
    return entry?.meshCutMask;
}

/// <summary>
/// 指定したRenderer/Submeshのメッシュカットマスクを設定
/// </summary>
private void SetMeshCutMask(int rendererIndex, int submeshIndex, Texture2D mask)
{
    var entry = materialSelections.Find(e => e.rendererIndex == rendererIndex && e.submeshIndex == submeshIndex);
    if (entry == null)
    {
        entry = new MaterialSelectionEntry(rendererIndex, submeshIndex, true);
        entry.meshCutMask = mask;
        materialSelections.Add(entry);
    }
    else
    {
        entry.meshCutMask = mask;
    }
}
```

---

### Task 6: プレビュー対応 — LayoutHash にマスクを含める

**Files:**
- Modify: `Editor/NDMF/ChimeraHairMasterPreview.cs:68-76`

**Step 1: ComputeLayoutHash にマスクの InstanceID を追加**

変更前 (L68-76):
```csharp
if (component.materialSelections != null)
{
    foreach (var entry in component.materialSelections)
    {
        hash = hash * 31 + entry.rendererIndex;
        hash = hash * 31 + entry.submeshIndex;
        hash = hash * 31 + entry.isIncluded.GetHashCode();
    }
}
```

変更後:
```csharp
if (component.materialSelections != null)
{
    foreach (var entry in component.materialSelections)
    {
        hash = hash * 31 + entry.rendererIndex;
        hash = hash * 31 + entry.submeshIndex;
        hash = hash * 31 + entry.isIncluded.GetHashCode();
        hash = hash * 31 + (entry.meshCutMask != null ? entry.meshCutMask.GetInstanceID() : 0);
    }
}
```

---

### Task 7: プレビュー対応 — ProcessComponentAtlas でマスクカットを適用

**Files:**
- Modify: `Editor/NDMF/ChimeraHairMasterPreview.cs` (ProcessComponentAtlas 内、UVリマップ後)

**Step 1: UVリマップ後にマスクカットを適用**

ProcessComponentAtlas メソッド内（L1054-1059 付近）で `MeshUVRemapper.RemapUVsByIslands()` の後に追加:

```csharp
// UVリマップ後のメッシュにマスクカットを適用
// 注: UVリマップ前の元メッシュUVでマスクをサンプリングする必要があるため、
//      リマップ前のメッシュを使用する
```

**重要な気づき:** ProcessComponentAtlas では UVリマップ後のメッシュを使うが、
マスクは元のUV座標に対して塗られている。
したがって、マスクカットは UVリマップ **前** に適用する必要がある。

ProcessComponentAtlas 内で `RemapUVsByIslands` の **前** にマスクカットを実行:

```csharp
// マスクカット適用（UVリマップ前）
foreach (var entry in component.materialSelections)
{
    if (!entry.isIncluded || entry.meshCutMask == null) continue;
    if (entry.rendererIndex != i) continue; // 現在のRendererのみ

    var meshForCut = renderer.sharedMesh;
    // まだ複製されていなければ複製
    if (!newMeshes.ContainsKey(renderer.GetInstanceID()))
    {
        meshForCut = Object.Instantiate(renderer.sharedMesh);
        newMeshes[renderer.GetInstanceID()] = meshForCut;
    }
    else
    {
        meshForCut = newMeshes[renderer.GetInstanceID()];
    }

    MeshCutter.ApplyPreviewCut(meshForCut, entry.submeshIndex, entry.meshCutMask);
}
```

---

### Task 8: プレビュー対応 — enableMeshMerge=false 時のマスクカットプレビュー

**Files:**
- Modify: `Editor/NDMF/ChimeraHairMasterPreview.cs` (OnFrame メソッド内)

**注:** 設計では `enableMeshMerge=false` 時のカットは今回のスコープ外としたため、
このタスクはスキップする。将来のニーズに応じて追加。

---

## 検証手順（Unity Editor でのローカルテスト）

1. Unity プロジェクトでシーンを開き、CHM コンポーネントを持つアバターを用意
2. 対象の髪メッシュ Renderer を CHM に追加
3. 「メッシュ統合」を有効にする
4. マテリアル選択で対象サブメッシュの「カットマスク」にマスクテクスチャを設定
   - mask-creation-tool で白黒マスクを作成（白=残す、黒=削除）
5. プレビューで除外部分が非表示になることを確認
6. ビルド（Play モードまたはアップロード）で統合結果を確認

## この設計が破綻するケース

- マスクで UV アイランドを中途半端に切った場合、アイランド境界が不自然になる可能性がある
  （ユーザーのマスク作成次第であり、ツール側の制約として許容範囲）
