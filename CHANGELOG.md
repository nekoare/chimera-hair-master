# Changelog

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
