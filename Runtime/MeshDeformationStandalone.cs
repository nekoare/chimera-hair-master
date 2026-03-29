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
    }
}
