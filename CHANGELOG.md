# Changelog

## v1.5.0

### 新機能

- **塗り感統一**
  - お手本 Renderer の質感（線の細さ・塗りの濃淡）を他 Renderer に転送
  - ラプラシアンピラミッドで 2 バンド（細・中）に分解し、各バンドのコントラスト振幅を独立に rescale
  - target 自身の毛束位置・密度は維持したまま、メリハリだけ reference に寄せる
  - Inspector「色合わせ」セクション内に新セクション「塗り感統一」を追加
    - 有効化トグル、お手本 Renderer、線の細さ強度、塗りの濃淡強度、対象スケール
  - ビルド・プレビュー両対応（cache 経路 / temp material 経路）
  - 4 言語対応 (ja-JP / en-US / zh-Hans / ko-KR)
  - 既知の限界: 周波数帯域内のスペクトルは変えないため、構造変換（細い strand を太くする等）はできない
- **メッシュ変形を Blendshape として出力**
  - メッシュ変形 Inspector の保存ボタン上に「Blendshape として出力」トグル + 名前フィールド
  - 「メッシュを保存」/「メッシュを保存して入れ替え」時に焼き込みではなく Blendshape として末尾追加
  - 既定 Blendshape 名: `CHMDeform`（同名衝突時は `CHMDeform_1` のように unique 化）
  - 入れ替え時は `SetBlendShapeWeight(100)` で見た目維持 + deltas クリア
  - VRChat アニメーション/エクスプレッションから weight を動かして変形 ON/OFF・部分適用可能
  - エクスポート（一括）・Prefab 出力でも本トグル設定を尊重
  - 4 言語対応 (ja / en / zh-Hans / ko)
  - 既知の制約: ビルドパス (NDMF MeshDeformPass) は焼き込み専用。Blendshape 化したい場合はビルド前に手動で「保存して入れ替え」する運用（トグル ON で未入れ替えなら警告ログ）

### 改善

- **「塗りの細かさを調整」→「ぼかし・シャープ調整」にリネーム**
  - 実装内容（負の値でブラー、正の値でシャープ）を直接表現する名称に変更
  - 4 言語対応 (ja / en / zh-Hans / ko)
- **メッシュ変形「メッシュを保存」ダイアログの初期フォルダを元メッシュ隣に**
  - 元メッシュと同じフォルダがデフォルト保存先になり、クリック数が削減
- **編集中にメッシュ変形コンポーネントを削除しても Mesh が Missing にならないように**
  - `ChimeraHairMaster` / `MeshDeformationStandalone` に `[ExecuteAlways]` + `OnDestroy` を追加
  - 編集中削除時に renderer.sharedMesh を originalMesh に自動復元

### API 変更

- `IMeshDeformationTarget` に `ExportAsBlendshape` / `BlendshapeName` プロパティ追加
- `MeshDeformer.ExportDeformedMeshAsBlendshape(renderer, deformation, name, out actualName)` 追加

### 内部

- `Runtime/StrandPatternSettings.cs` 新規
- `Editor/Shader/StrandPatternHighPass.compute`, `StrandPatternCompose.compute` 新規
- `Editor/Processing/StrandPatternExtractor.cs`, `StrandPatternComposer.cs`, `StrandPatternApplier.cs` 新規
- `Editor/NDMF/ColorTransformPass.cs`, `ChimeraHairMasterPreview.cs` に塗り感統一適用フックを追加
- Tests/Editor に `StrandPatternExtractorTests.cs`, `StrandPatternComposerTests.cs` 追加

## v1.4.4

### 改善

- **アバター自動選択のヒント表示を追加**
  - 対象選択セクションのアバター欄直下に「※アバターは髪パーツから自動選択されます」を常時表示
  - v1.4.0 以降アバタードロップが不要になったことを明確化し、問い合わせを削減
  - 4 言語（ja-JP / en-US / zh-Hans / ko-KR）対応

## v1.4.3

### 改善

- **Prefab出力を色変換オフ時にも利用可能に**
  - 従来は色変換 ON 時のみ「エクスポート」セクションが表示されていたが、色変換オフ時も Prefab 出力を可能化
  - 色変換オフ時の Prefab 出力では「マテリアル統一（任意）・メッシュ変形反映（任意）・不要 bone 整理」が実行される
  - 「不要 bone 整理だけ目的の独立 Prefab 化」「メッシュ変形だけ確定した Prefab」などのユースケースに対応
- **Prefab出力に「マテリアル設定を統一する」トグル追加**
  - デフォルト ON（従来挙動と同等）
  - OFF 時は基準マテリアルからの shader 設定コピーをスキップし、各 Renderer の元マテリアル設定をそのまま保持
  - `ChimeraHairMaster.unifyMaterialSettings` フィールドとしてアバターごとに永続化

## v1.4.2

### 新機能

- **Prefab出力機能**
  - 色合わせ・マテリアル設定・変形情報を反映した髪パーツのPrefabを生成
  - 元マテリアル / 元テクスチャ / 元アバターは書換しない非破壊エクスポート
  - 元マテリアル単位で重複排除した clone マテリアルを `{元名}_CHM.mat` として元マテリアル同フォルダに保存
  - 必要な bone・PhysBone・Constraint を含む単独 Prefab を生成
  - ModularAvatar 検出時は各髪 Armature root に `MA Merge Armature` を自動付与（asmdef versionDefines による条件コンパイル）
  - 生成 Prefab はアバターと同じシーンのルート直下に自動配置
- **Inspector「エクスポート」セクション追加**
  - 既存「テクスチャ出力」と新規「Prefab出力」を 1 つの Foldout に統合
  - 「非破壊処理が不要な場合、他ツールと併用して問題がある場合はエクスポートしてから試してください」のヒントを表示
  - サブセクション「Prefab出力」には「メッシュ変形を適用」トグル（デフォルト ON）

### 改善

- ローカライズ拡充
  - エクスポート関連の文字列を 4 言語（ja-JP / en-US / zh-Hans / ko-KR）に対応

### 内部整理

- `ColorApplier` の以下メソッドを `internal` に格上げ（`PrefabExporter` から再利用）
  - `DetermineSourceColor`、`GetRendererBrightnessOffset`、`GetRendererBlurSharp`、`CopyTextureImportSettings`、`DecompressTexture`、`CopyMatCapTextures`
- `Editor/Processing/HierarchyDependencyResolver.cs` 新規追加（bone・PhysBone・Constraint 依存解決と不要 GameObject 削除）
- `Editor/Processing/PrefabExporter.cs` 新規追加
- asmdef の `versionDefines` に `nadena.dev.modular-avatar` 検出シンボル `CHM_MODULAR_AVATAR` を追加（MA 未導入でもコンパイル可能）

## v1.4.1

### バグ修正

- **Oklab 色合わせ: 単色寄りテクスチャでムラが出る問題を修正**
  - source の L 範囲が極端に狭いテクスチャ（フラットな色味のメッシュ）で、線形リマップの slope が大きくなり、元テクスチャの微小な陰影が増幅されてムラとして視認される現象を修正
  - sourceRange に応じて、線形リマップと加算オフセット方式を滑らかに切替えるロジックを導入（threshold = 0.20）
  - 通常の range のテクスチャでは従来挙動を維持

## v1.4.0

### 新機能

- **色合わせ Oklab アルゴリズム追加**
  - 色指定モードに「HSV（従来）」と「Oklab」の選択肢を追加
  - Oklab は輝度と彩度が独立した色空間で、複数テクスチャの色味を target に自動で揃えやすい
  - UV 使用領域の上位 5% 輝度を基準に線形リマップする設計
- **RGB差分モード（Mode 3）追加**
  - テクスチャ代表色から target への RGB差分を全ピクセルに加算
  - 色相を強制せず、ハイライト等のニュアンスを残したまま target に寄せたい用途向け
- **塗りの細かさを調整**
  - Renderer ごとにブラー（-1）〜 シャープ（+1）の前処理を適用可能
  - 髪テクスチャ間の塗り感（カリカリ vs 柔らかい）を揃えたい場合に使用
  - ブラー側はダウンサンプル方式で -1 では単色化レベルの強いブラー
- **UV 端のテクスチャ塗り足し（Edge Dilation）**
  - 色変換後にメッシュ UV 使用領域外を近隣色で拡張し、bilinear / mipmap で UV 端が暗くならないように
  - Compute Shader 実装で高速
- **UV 配置プレビュー拡大ビュー**
  - UV 配置エリアに「拡大ビュー」ボタンを追加
  - 別ウィンドウで大きなプレビューを開き、同じドラッグ操作が可能
- **アバター自動検出**
  - 対象選択エリアの「アバター」欄を廃止し、髪パーツから親 `VRCAvatarDescriptor` を自動検出
- **テクスチャ出力機能がメッシュ統合有効時にも利用可能に**
  - 適用時にメッシュ統合設定は無効化される（確認ダイアログあり）

### 改善

- **lilToon 輪郭線シェーダー切替に対応**
  - previewMaterial で輪郭線 ON/OFF を切り替えると、出力マテリアルの shader も自動で切り替わるように
- **Inspector UI 整理**
  - 色合わせ設定を枠付きで整理、サブセクション（個別の明るさ、塗りの細かさ、色合わせ無視マスク）も枠で区切り
  - 色変換モードを Mode 1 / Mode 2 / Mode 3 の順に統一
  - メッシュ変形機能のオン/オフトグルを削除（データがあれば動作）
- **ローカライズ拡充**
  - 新機能関連の文字列を 4 言語（ja-JP / en-US / zh-Hans / ko-KR）に対応
- **Oklab の Gamut Mapping を緩和**
  - わずかな float 誤差による過剰な彩度縮小を防ぐよう ε 閾値を導入
- **色変換キャッシュ構造を強化**
  - Pass 単位のピクセル読み込みキャッシュを導入し、同一テクスチャを複数サブメッシュで共有する場合の読み直しを削減

### 破壊的変更

- **輝度統一機能 (`BrightnessUnifyMode`) を廃止**
  - 代わりに「Oklab アルゴリズム」が自動で明度統一を行う
  - v1.3.2 以前で輝度統一を設定していたプロジェクトは、その設定が無視されます
- **`hueShiftAlgorithm` のデフォルトは Oklab**
  - 新規作成される CHM コンポーネントは Oklab で開始
  - 既存プロジェクト（v1.3.2 以前）はシリアライズの性質上 HSV 相当で開き、従来挙動を維持

### 内部整理

- `ColorTransformSettings.OklabSourceCDominant` の未使用フィールドを削除
- 旧 `BrightnessUnifyMode` 関連のローカライズキーを削除
- `OklabConverter` ユニットテストを追加（Tests/Editor）

## v1.3.2 以前

詳細は git history を参照。
