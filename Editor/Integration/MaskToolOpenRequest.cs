using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// マスクツールを開く際のリクエストデータ
    /// CHM独自の型として定義（マスクツール側のアセンブリに依存しない）
    /// </summary>
    public class MaskToolOpenRequest
    {
        public SkinnedMeshRenderer targetRenderer;
        public GameObject targetGameObject;
        public Mesh atlasMesh;
        public List<MaskToolSourceEntry> sourceMasks;
        public int atlasResolution;
        public List<Renderer> sourceRenderers;
        public string currentMaskSlotName;

        public Action<RenderTexture> onMaskTextureAvailable;
        public Action onMaskToolClosed;
        public Action<string, string> onMaskApplied;
        public Action<string> onOutputSlotChanged;
    }

    /// <summary>
    /// 元Rendererが持っていたマスク情報（1アイランド分）
    /// </summary>
    public class MaskToolSourceEntry
    {
        public Texture2D maskTexture;
        public Texture2D mainTexture;
        public Rect originalBounds;
        public Vector2 atlasPosition;
        public Vector2 atlasScale;
        public int sourceRendererIndex;
        public int sourceMaterialIndex;
    }
}
