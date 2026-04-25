using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace ChimeraHairMaster
{
    /// <summary>
    /// メッシュ変形スタンドアロンコンポーネント。
    /// ChimeraHairMaster の色合わせ・メッシュ統合を使わず、
    /// メッシュ変形機能だけを独立して使用するためのコンポーネント。
    /// 1コンポーネントにつき1Rendererを対象とする。
    /// </summary>
    [AddComponentMenu("Chimera Hair Master/CHM - メッシュ変形機能")]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class MeshDeformationStandalone : MonoBehaviour, IEditorOnly, IMeshDeformationTarget
    {
        [SerializeField]
        public SkinnedMeshRenderer targetRenderer;

        [SerializeField]
        public List<RendererDeformation> rendererDeformations = new List<RendererDeformation>();

        [SerializeField]
        public int deformEditingRendererIndex = -1;

        [SerializeField]
        public Mesh deformOriginalMesh;

        [SerializeField]
        public bool exportAsBlendshape = false;

        [SerializeField]
        public string blendshapeName = "CHMDeform";

        // IMeshDeformationTarget 実装
        // SceneEditor はインデックス0で単一Rendererにアクセスする
        [System.NonSerialized]
        private List<SkinnedMeshRenderer> _targetRenderersList;

        public List<SkinnedMeshRenderer> DeformTargetRenderers
        {
            get
            {
                _targetRenderersList ??= new List<SkinnedMeshRenderer>(1);
                if (_targetRenderersList.Count == 0)
                    _targetRenderersList.Add(targetRenderer);
                else
                    _targetRenderersList[0] = targetRenderer;
                return _targetRenderersList;
            }
        }

        public List<RendererDeformation> RendererDeformations => rendererDeformations;

        public int DeformEditingRendererIndex
        {
            get => deformEditingRendererIndex;
            set => deformEditingRendererIndex = value;
        }

        public Mesh DeformOriginalMesh
        {
            get => deformOriginalMesh;
            set => deformOriginalMesh = value;
        }

        public UnityEngine.Object UndoTarget => this;

        public bool ExportAsBlendshape
        {
            get => exportAsBlendshape;
            set => exportAsBlendshape = value;
        }

        public string BlendshapeName
        {
            get => blendshapeName;
            set => blendshapeName = value;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 編集中にコンポーネント / GameObject が削除された時の保険:
        /// renderer.sharedMesh が編集中の workingMesh のままになっていたら、保持していた originalMesh で復元する
        /// （[ExecuteAlways] によりエディタモードでも OnDestroy が呼ばれる）
        /// </summary>
        private void OnDestroy()
        {
            if (Application.isPlaying) return;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (deformEditingRendererIndex >= 0 && deformOriginalMesh != null && targetRenderer != null)
            {
                targetRenderer.sharedMesh = deformOriginalMesh;
                UnityEditor.EditorUtility.SetDirty(targetRenderer);
            }
        }
#endif
    }
}
