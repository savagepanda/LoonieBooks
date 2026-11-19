# Tesseract.js (vendored)

In-browser OCR for the receipt scanner. Served from this site so receipt photos and the OCR engine
never touch a third-party CDN.

| Files | Package | Licence |
|---|---|---|
| `tesseract.min.js`, `worker.min.js` | `tesseract.js@7.0.0` (`dist/`) | Apache-2.0 |
| `core/tesseract-core-{relaxedsimd-lstm,simd-lstm,lstm}.wasm.js` | `tesseract.js-core@7.0.0` | Apache-2.0 |
| `lang/eng.traineddata.gz` | `@tesseract.js-data/eng@1.0.0` (`4.0.0_best_int/`) | package MIT; models Apache-2.0 (tesseract-ocr/tessdata) |
| `lang/fra.traineddata.gz` | `@tesseract.js-data/fra@1.0.0` (`4.0.0_best_int/`) | package MIT; models Apache-2.0 (tesseract-ocr/tessdata) |

Downloaded from registry.npmjs.org on 2026-11-14; each tarball matched its npm `sha512` integrity.
The worker picks one of the three cores by the browser's WebAssembly SIMD support. To upgrade,
replace all of these files from matching versions.
