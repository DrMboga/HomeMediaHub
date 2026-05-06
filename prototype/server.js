const http = require('http');
const fs   = require('fs');
const path = require('path');

const PORT      = 3333;
const THUMB_DIR = 'C:\\Users\\Mike\\Projects\\thumbnails';
const PHOTO_DIR = 'C:\\Users\\Mike\\Projects\\TestPhoto';
const PROTO_DIR = __dirname;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js':   'application/javascript',
  '.css':  'text/css',
  '.webp': 'image/webp',
  '.png':  'image/png',
  '.jpg':  'image/jpeg', '.jpeg': 'image/jpeg', '.JPG': 'image/jpeg',
  '.json': 'application/json',
  '.ico':  'image/x-icon',
  '.mp4':  'video/mp4',   '.MP4': 'video/mp4',
  '.mov':  'video/quicktime', '.MOV': 'video/quicktime',
};

function serveFile(res, fullPath, contentType) {
  const stat = fs.statSync(fullPath);
  res.setHeader('Content-Type', contentType);
  res.setHeader('Accept-Ranges', 'bytes');
  res.setHeader('Content-Length', stat.size);
  res.writeHead(200);
  fs.createReadStream(fullPath).pipe(res);
}

function serveRange(req, res, fullPath, contentType) {
  const stat = fs.statSync(fullPath);
  const range = req.headers.range;
  if (!range) { serveFile(res, fullPath, contentType); return; }
  const [startStr, endStr] = range.replace('bytes=','').split('-');
  const start = parseInt(startStr, 10);
  const end   = endStr ? parseInt(endStr, 10) : stat.size - 1;
  res.setHeader('Content-Type', contentType);
  res.setHeader('Content-Range', `bytes ${start}-${end}/${stat.size}`);
  res.setHeader('Accept-Ranges', 'bytes');
  res.setHeader('Content-Length', end - start + 1);
  res.writeHead(206);
  fs.createReadStream(fullPath, { start, end }).pipe(res);
}

http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const p   = url.pathname;

  res.setHeader('Access-Control-Allow-Origin', '*');

  // Thumbnails (WebP by GUID)
  if (p.startsWith('/thumbs/')) {
    const full = path.join(THUMB_DIR, path.basename(p));
    if (fs.existsSync(full)) {
      res.setHeader('Cache-Control', 'public, max-age=86400');
      serveFile(res, full, 'image/webp');
    } else { res.writeHead(404); res.end(); }
    return;
  }

  // Original photos and videos from TestPhoto
  if (p.startsWith('/originals/')) {
    const rel  = decodeURIComponent(p.slice('/originals/'.length));
    const full = path.join(PHOTO_DIR, rel);
    if (fs.existsSync(full) && fs.statSync(full).isFile()) {
      const ext  = path.extname(full).toLowerCase();
      const mime = MIME[ext] || MIME[path.extname(full)] || 'application/octet-stream';
      const isVideo = ['.mp4','.mov'].includes(ext);
      if (isVideo) serveRange(req, res, full, mime);
      else         serveFile(res, full, mime);
    } else { res.writeHead(404); res.end(); }
    return;
  }

  // Static prototype files
  const filePath = p === '/' ? path.join(PROTO_DIR, 'index.html')
                             : path.join(PROTO_DIR, p);
  if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
    res.setHeader('Content-Type', MIME[path.extname(filePath)] || 'text/plain');
    fs.createReadStream(filePath).pipe(res);
  } else {
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    fs.createReadStream(path.join(PROTO_DIR, 'index.html')).pipe(res);
  }
}).listen(PORT, () => console.log(`Prototype → http://localhost:${PORT}`));
