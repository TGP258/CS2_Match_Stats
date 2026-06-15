const express = require('express');
const fs = require('fs');
const path = require('path');

const app = express();
const PORT = 5173;

// 默认路径 - 请根据您的实际安装位置修改
// 例如: C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global Offensive\\game\\csgo\\match_history
// 或: ../../game/csgo/match_history (相对于 web 目录)
const MATCH_DIR = process.env.MATCH_HISTORY_DIR || path.join(__dirname, '../../game/csgo/match_history');

console.log('========================================');
console.log('  CS2 Match Stats - Web Viewer');
console.log('========================================');
console.log('  Serving from: ' + MATCH_DIR);
console.log('  Web UI:      http://localhost:' + PORT);
console.log('  API:         http://localhost:' + PORT + '/api/matches');
console.log('========================================');

// 确保目录存在
if (!fs.existsSync(MATCH_DIR)) {
  console.log('\n  [!] 对局记录目录不存在，正在创建...');
  try {
    fs.mkdirSync(MATCH_DIR, { recursive: true });
    console.log('  [✓] 目录已创建');
  } catch (e) {
    console.log('  [✗] 无法创建目录: ' + e.message);
  }
}

app.use(express.json());
app.use(express.static('public'));

app.get('/api/matches', (req, res) => {
  try {
    if (!fs.existsSync(MATCH_DIR)) {
      return res.json([]);
    }

    const files = fs.readdirSync(MATCH_DIR)
      .filter(file => file.startsWith('match_') && file.endsWith('.json'))
      .map(file => {
        const filePath = path.join(MATCH_DIR, file);
        const stat = fs.statSync(filePath);
        try {
          const data = fs.readFileSync(filePath, 'utf8');
          const match = JSON.parse(data);
          return {
            filename: file,
            timestamp: stat.mtime.toISOString(),
            mapName: match.MapName || 'Unknown',
            duration: match.Duration || 0,
            roundCount: match.Rounds?.length || 0
          };
        } catch {
          return {
            filename: file,
            timestamp: stat.mtime.toISOString(),
            mapName: 'Unknown',
            duration: 0,
            roundCount: 0
          };
        }
      })
      .sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));

    res.json(files);
  } catch (error) {
    console.error('[Error] Reading matches:', error);
    res.status(500).json({ error: 'Failed to read matches' });
  }
});

app.get('/api/match/:filename', (req, res) => {
  try {
    const filename = req.params.filename;

    if (!filename.startsWith('match_') || !filename.endsWith('.json')) {
      return res.status(400).json({ error: 'Invalid filename' });
    }

    const filePath = path.join(MATCH_DIR, filename);

    if (!fs.existsSync(filePath)) {
      return res.status(404).json({ error: 'Match not found' });
    }

    const data = fs.readFileSync(filePath, 'utf8');
    const match = JSON.parse(data);
    res.json(match);
  } catch (error) {
    console.error('[Error] Reading match:', error);
    res.status(500).json({ error: 'Failed to read match' });
  }
});

app.get('/api/config', (req, res) => {
  res.json({
    matchDir: MATCH_DIR,
    port: PORT
  });
});

app.listen(PORT, () => {
  console.log('\n  [✓] 服务器已启动');
  console.log('  [ℹ] 如对局显示为空，请修改 web/server.js 中的 MATCH_DIR 路径');
});
