#!/usr/bin/env node
// Downloads the models of the face recognition into src/MatPhoto/models (for a developer; the Dockerfile downloads them into the image itself):
//   YuNet (face detection, MIT) and SFace (face recognition, Apache 2.0) of the OpenCV model zoo, https://github.com/opencv/opencv_zoo
//   node scripts/models.js
// Each file is checked against its SHA-256; a file that is there and right is not downloaded again.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const models = [
  {
    file: 'face_detection_yunet_2023mar.onnx',
    url: 'https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx',
    sha256: '8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4',
  },
  {
    file: 'face_recognition_sface_2021dec.onnx',
    url: 'https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx',
    sha256: '0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79',
  },
];

const target = path.join(__dirname, '..', 'src', 'MatPhoto', 'models');
const hash = data => crypto.createHash('sha256').update(data).digest('hex');

(async () => {
  fs.mkdirSync(target, { recursive: true });
  for (const m of models) {
    const file = path.join(target, m.file);
    if (fs.existsSync(file) && hash(fs.readFileSync(file)) === m.sha256) { console.log(`${m.file}: there`); continue; }
    const response = await fetch(m.url);
    if (!response.ok) throw new Error(`${m.file}: HTTP ${response.status}`);
    const data = Buffer.from(await response.arrayBuffer());
    if (hash(data) !== m.sha256) throw new Error(`${m.file}: the checksum does not match`);
    fs.writeFileSync(file, data);
    console.log(`${m.file}: ${(data.length / 1048576).toFixed(1)} MB`);
  }
})().catch(e => { console.error(e.message); process.exit(1); });
