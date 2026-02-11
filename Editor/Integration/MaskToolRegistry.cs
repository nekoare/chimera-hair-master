namespace ChimeraHairMaster.Editor
{
    /// <summary>
    /// マスクツールのfallbackレジストリ
    /// com.nekoare.mask-creation-tool (UPM版) がない場合に、
    /// Assets版マスクツールが自身をハンドラとして登録する
    /// </summary>
    public static class MaskToolRegistry
    {
        public delegate void OpenMaskToolHandler(MaskToolOpenRequest request);

        private static OpenMaskToolHandler _handler;

        /// <summary>
        /// fallbackハンドラが登録されているかどうか
        /// </summary>
        public static bool HasHandler => _handler != null;

        /// <summary>
        /// マスクツールのfallbackハンドラを登録する
        /// </summary>
        public static void Register(OpenMaskToolHandler handler)
        {
            _handler = handler;
        }

        /// <summary>
        /// 登録を解除する
        /// </summary>
        public static void Unregister()
        {
            _handler = null;
        }

        /// <summary>
        /// 登録されたハンドラでマスクツールを開く
        /// </summary>
        public static void Open(MaskToolOpenRequest request)
        {
            _handler?.Invoke(request);
        }
    }
}
