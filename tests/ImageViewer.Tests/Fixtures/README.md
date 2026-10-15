# テスト用の画像（コミットしてあるもの）

WIC（Windows の画像 API）で作れない画像だけを置く。ほかのテスト用の画像は、テストの中で `TestImage` を使って作る。

| ファイル | 中身 | 使うテスト |
|---|---|---|
| `anim.webp` | 80×40、3 コマ（赤 → 左半分だけ青 → 緑）。表示時間 5 / 200 / 500 ms。可逆圧縮 | AnimationTests |
| `rotated.webp` | `anim.webp` と同じ 3 コマに、EXIF の向き 6（時計回りに 90° 回すと正立）を付けたもの | AnimationTests |

どちらも ImageSharp 3.1.12 の `WebpEncoder`（`FileFormat = Lossless`）で作った。作り直すときは、同じ中身になるように作る。
