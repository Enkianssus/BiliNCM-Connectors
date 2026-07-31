const CORE_PROJECTS = {
  awoo: {
    name: '嗷呜点歌机 1.1.x',
    repo: 'Enkianssus/AwooMusicBot',
    versionPrefix: '1.1.',
    exeName: 'awoo-musicbot-win-Portable.zip',
    badge: '推荐 · 新架构',
    featured: true,
    description:
      '支持网易云、酷狗、QQ 音乐与 Folia，提供游客模式、独立连接器、只读 HTTP/WebSocket 接口和问题反馈。功能更多、体验更好，但新架构仍可能存在播放器兼容性或稳定性问题。'
  },
  bilincm: {
    name: 'BiliNCM 1.0.x（旧稳定版）',
    repo: 'Enkianssus/AwooMusicBot',
    versionPrefix: '1.0.',
    exeName: 'bilincm-win-Portable.zip',
    badge: '维护通道',
    featured: false,
    description:
      '仅面向原有网易云使用流程，功能较少，但经过更长时间验证。继续接收 1.0.x 修复，不会自动升级到 1.1.x。'
  }
};

const CONNECTOR_REPO = 'Enkianssus/BiliNCM-Connectors';
const CONNECTOR_IDS = new Set(['netease', 'kugou', 'qqmusic', 'folia']);
const GITHUB_HOSTS = new Set([
  'github.com',
  'api.github.com',
  'raw.githubusercontent.com',
  'objects.githubusercontent.com',
  'release-assets.githubusercontent.com',
  'githubreleases.com'
]);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === 'OPTIONS') {
      return new Response(null, {
        status: 204,
        headers: corsHeaders()
      });
    }

    if (url.pathname === '/' || url.pathname === '/index.html') {
      return htmlResponse(renderHome(url.host));
    }

    if (url.pathname === '/feedback' && request.method === 'GET') {
      return htmlResponse(renderFeedback());
    }

    if (url.pathname === '/feedback/admin' && request.method === 'GET') {
      return htmlResponse(renderFeedbackAdmin());
    }

    if (url.pathname === '/api/v1/feedback' && request.method === 'POST') {
      return createFeedback(request, env);
    }

    const publicFeedback = url.pathname.match(
      /^\/api\/v1\/feedback\/([A-Z0-9-]{8,40})$/
    );
    if (publicFeedback && request.method === 'GET') {
      return getPublicFeedback(publicFeedback[1], env);
    }

    if (
      url.pathname === '/api/v1/admin/feedback'
      && request.method === 'GET'
    ) {
      return listFeedback(request, env);
    }

    const adminFeedback = url.pathname.match(
      /^\/api\/v1\/admin\/feedback\/([0-9a-f-]{36})$/
    );
    if (adminFeedback && request.method === 'PATCH') {
      return updateFeedback(request, adminFeedback[1], env);
    }

    if (url.pathname === '/connectors/v1/catalog.json') {
      return proxyCatalog(request);
    }

    const connectorDownload = url.pathname.match(
      /^\/connectors\/v1\/download\/(netease|kugou|qqmusic|folia)\/([0-9]+\.[0-9]+\.[0-9]+)\/([^/]+)$/
    );
    if (connectorDownload) {
      const [, connectorId, version, assetName] = connectorDownload;
      const validAsset = new RegExp(
        `^bilincm-connector-${connectorId}-${version}-win-x(86|64)\\.zip$`
      ).test(assetName);
      if (!CONNECTOR_IDS.has(connectorId) || !validAsset) {
        return jsonResponse(
          { error: 'Invalid connector asset.' },
          400
        );
      }

      return proxyGitHub(
        request,
        `https://github.com/${CONNECTOR_REPO}/releases/download/`
          + `${connectorId}-v${version}/${assetName}`,
        {
          downloadName: assetName,
          cacheControl: 'public, max-age=31536000, immutable'
        }
      );
    }

    const directDownload = url.pathname.match(/^\/download\/([^/]+)$/);
    if (directDownload) {
      const project = CORE_PROJECTS[directDownload[1]];
      if (!project) {
        return new Response('项目不存在', { status: 404 });
      }

      return proxyCoreAsset(
        request,
        project,
        project.exeName,
        { downloadName: project.exeName }
      );
    }

    const coreUpdate = url.pathname.match(
      /^\/update\/([^/]+)\/(.+)$/
    );
    if (coreUpdate) {
      const project = CORE_PROJECTS[coreUpdate[1]];
      if (!project) {
        return new Response('项目不存在', { status: 404 });
      }

      return proxyCoreAsset(request, project, coreUpdate[2]);
    }

    const genericTarget = parseGenericProxyTarget(request, url);
    if (genericTarget) {
      return proxyGitHub(request, genericTarget);
    }

    return htmlResponse(renderHome(url.host));
  }
};

const FEEDBACK_CATEGORIES = new Set([
  'bug',
  'connector',
  'compatibility',
  'feature',
  'other'
]);
const FEEDBACK_PRIORITIES = new Set([
  'low',
  'normal',
  'high',
  'critical'
]);
const FEEDBACK_STATUSES = new Set([
  'open',
  'triaging',
  'working',
  'resolved',
  'closed',
  'duplicate'
]);

async function createFeedback(request, env) {
  if (!env.FEEDBACK_DB) {
    return jsonResponse({ error: 'Feedback database is unavailable.' }, 503);
  }

  let body;
  try {
    body = await readJsonBody(request, 64 * 1024);
  } catch (error) {
    return jsonResponse({ error: String(error.message || error) }, 400);
  }

  const title = cleanText(body.title, 120);
  const description = cleanText(body.description, 8000);
  if (title.length < 4 || description.length < 10) {
    return jsonResponse(
      { error: '标题至少 4 个字符，问题描述至少 10 个字符。' },
      400
    );
  }

  const category = FEEDBACK_CATEGORIES.has(body.category)
    ? body.category
    : 'bug';
  const priority = FEEDBACK_PRIORITIES.has(body.priority)
    ? body.priority
    : 'normal';
  const diagnostics =
    body.diagnostics && typeof body.diagnostics === 'object'
      ? body.diagnostics
      : {};
  const diagnosticsJson = JSON.stringify(diagnostics);
  if (new TextEncoder().encode(diagnosticsJson).length > 48 * 1024) {
    return jsonResponse({ error: '诊断信息超过 48 KB。' }, 400);
  }

  const now = new Date().toISOString();
  const ip = request.headers.get('CF-Connecting-IP') || 'unknown';
  const ipHash = await sha256Hex(
    `${env.FEEDBACK_IP_SALT || 'feedback-v1'}\n${now.slice(0, 10)}\n${ip}`
  );
  const rate = await env.FEEDBACK_DB.prepare(
    'SELECT COUNT(*) AS count FROM feedback WHERE ip_hash = ? AND created_at >= ?'
  ).bind(ipHash, `${now.slice(0, 10)}T00:00:00.000Z`).first();
  if (Number(rate?.count || 0) >= 8) {
    return jsonResponse(
      { error: '今天提交次数较多，请稍后再试。' },
      429
    );
  }

  const id = crypto.randomUUID();
  const publicId = makePublicFeedbackId(now);
  const values = {
    contact: cleanText(body.contact, 200),
    source: cleanEnum(body.source, ['app', 'web'], 'web'),
    appVersion: cleanText(body.appVersion, 40),
    coreVersion: cleanText(body.coreVersion, 40),
    platform: cleanText(body.platform, 40),
    architecture: cleanText(body.architecture, 30),
    osVersion: cleanText(body.osVersion, 100),
    selectedPlayer: cleanText(body.selectedPlayer, 40),
    playerVersion: cleanText(body.playerVersion, 80),
    connectorId: cleanText(body.connectorId, 40),
    connectorVersion: cleanText(body.connectorVersion, 40),
    latestConnectorVersion: cleanText(
      body.latestConnectorVersion,
      40
    ),
    connectionStatus: cleanText(body.connectionStatus, 500),
    country: cleanText(request.cf?.country || '', 8),
    userAgent: cleanText(request.headers.get('User-Agent') || '', 300)
  };

  await env.FEEDBACK_DB.prepare(
    `INSERT INTO feedback (
      id, public_id, created_at, updated_at, category, status, priority,
      title, description, contact, source, app_version, core_version,
      platform, architecture, os_version, selected_player, player_version,
      connector_id, connector_version, latest_connector_version,
      connection_status, diagnostics_json, country, ip_hash, user_agent
    ) VALUES (
      ?, ?, ?, ?, ?, 'open', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
      ?, ?, ?, ?, ?
    )`
  ).bind(
    id,
    publicId,
    now,
    now,
    category,
    priority,
    title,
    description,
    values.contact,
    values.source,
    values.appVersion,
    values.coreVersion,
    values.platform,
    values.architecture,
    values.osVersion,
    values.selectedPlayer,
    values.playerVersion,
    values.connectorId,
    values.connectorVersion,
    values.latestConnectorVersion,
    values.connectionStatus,
    diagnosticsJson,
    values.country,
    ipHash,
    values.userAgent
  ).run();

  const origin = new URL(request.url).origin;
  return jsonResponse(
    {
      success: true,
      id: publicId,
      status: 'open',
      trackingUrl: `${origin}/feedback?id=${encodeURIComponent(publicId)}`
    },
    201
  );
}

async function getPublicFeedback(publicId, env) {
  if (!env.FEEDBACK_DB) {
    return jsonResponse({ error: 'Feedback database is unavailable.' }, 503);
  }
  const row = await env.FEEDBACK_DB.prepare(
    `SELECT public_id, created_at, updated_at, category, status, title,
            public_reply
       FROM feedback WHERE public_id = ?`
  ).bind(publicId).first();
  if (!row) {
    return jsonResponse({ error: '没有找到这条反馈。' }, 404);
  }
  return jsonResponse({
    id: row.public_id,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    category: row.category,
    status: row.status,
    title: row.title,
    reply: row.public_reply
  });
}

async function listFeedback(request, env) {
  const unauthorized = await requireFeedbackAdmin(request, env);
  if (unauthorized) return unauthorized;
  if (!env.FEEDBACK_DB) {
    return jsonResponse({ error: 'Feedback database is unavailable.' }, 503);
  }

  const url = new URL(request.url);
  const clauses = [];
  const bindings = [];
  const status = url.searchParams.get('status');
  const player = cleanText(url.searchParams.get('player'), 40);
  const query = cleanText(url.searchParams.get('q'), 100);
  if (status && FEEDBACK_STATUSES.has(status)) {
    clauses.push('status = ?');
    bindings.push(status);
  }
  if (player) {
    clauses.push('selected_player = ?');
    bindings.push(player);
  }
  if (query) {
    clauses.push('(title LIKE ? OR description LIKE ? OR public_id LIKE ?)');
    const pattern = `%${query}%`;
    bindings.push(pattern, pattern, pattern);
  }
  const limit = Math.min(
    100,
    Math.max(1, Number(url.searchParams.get('limit')) || 50)
  );
  const sql = `SELECT * FROM feedback
    ${clauses.length ? `WHERE ${clauses.join(' AND ')}` : ''}
    ORDER BY created_at DESC LIMIT ?`;
  bindings.push(limit);
  const result = await env.FEEDBACK_DB.prepare(sql)
    .bind(...bindings)
    .all();
  return jsonResponse({
    success: true,
    items: (result.results || []).map(toAdminFeedback)
  });
}

async function updateFeedback(request, id, env) {
  const unauthorized = await requireFeedbackAdmin(request, env);
  if (unauthorized) return unauthorized;
  if (!env.FEEDBACK_DB) {
    return jsonResponse({ error: 'Feedback database is unavailable.' }, 503);
  }

  let body;
  try {
    body = await readJsonBody(request, 16 * 1024);
  } catch (error) {
    return jsonResponse({ error: String(error.message || error) }, 400);
  }
  const existing = await env.FEEDBACK_DB.prepare(
    'SELECT * FROM feedback WHERE id = ?'
  ).bind(id).first();
  if (!existing) {
    return jsonResponse({ error: '反馈不存在。' }, 404);
  }

  const status = FEEDBACK_STATUSES.has(body.status)
    ? body.status
    : existing.status;
  const priority = FEEDBACK_PRIORITIES.has(body.priority)
    ? body.priority
    : existing.priority;
  const adminNote = body.adminNote === undefined
    ? existing.admin_note
    : cleanText(body.adminNote, 8000);
  const publicReply = body.publicReply === undefined
    ? existing.public_reply
    : cleanText(body.publicReply, 4000);
  const updatedAt = new Date().toISOString();

  await env.FEEDBACK_DB.prepare(
    `UPDATE feedback
        SET status = ?, priority = ?, admin_note = ?, public_reply = ?,
            updated_at = ?
      WHERE id = ?`
  ).bind(
    status,
    priority,
    adminNote,
    publicReply,
    updatedAt,
    id
  ).run();
  const updated = await env.FEEDBACK_DB.prepare(
    'SELECT * FROM feedback WHERE id = ?'
  ).bind(id).first();
  return jsonResponse({ success: true, item: toAdminFeedback(updated) });
}

async function requireFeedbackAdmin(request, env) {
  const expected = String(env.FEEDBACK_ADMIN_TOKEN || '');
  const authorization = request.headers.get('Authorization') || '';
  const supplied = authorization.startsWith('Bearer ')
    ? authorization.slice(7).trim()
    : '';
  if (
    !expected
    || !supplied
    || await sha256Hex(expected) !== await sha256Hex(supplied)
  ) {
    return jsonResponse({ error: '需要反馈后台管理令牌。' }, 401);
  }
  return null;
}

async function readJsonBody(request, maximumBytes) {
  const declared = Number(request.headers.get('Content-Length') || 0);
  if (declared > maximumBytes) {
    throw new Error('请求内容过大。');
  }
  const text = await request.text();
  if (new TextEncoder().encode(text).length > maximumBytes) {
    throw new Error('请求内容过大。');
  }
  try {
    return JSON.parse(text || '{}');
  } catch {
    throw new Error('请求不是有效的 JSON。');
  }
}

function toAdminFeedback(row) {
  if (!row) return null;
  let diagnostics = {};
  try {
    diagnostics = JSON.parse(row.diagnostics_json || '{}');
  } catch {
    diagnostics = { parseError: true };
  }
  return {
    id: row.id,
    publicId: row.public_id,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    category: row.category,
    status: row.status,
    priority: row.priority,
    title: row.title,
    description: row.description,
    contact: row.contact,
    source: row.source,
    appVersion: row.app_version,
    coreVersion: row.core_version,
    platform: row.platform,
    architecture: row.architecture,
    osVersion: row.os_version,
    selectedPlayer: row.selected_player,
    playerVersion: row.player_version,
    connectorId: row.connector_id,
    connectorVersion: row.connector_version,
    latestConnectorVersion: row.latest_connector_version,
    connectionStatus: row.connection_status,
    diagnostics,
    country: row.country,
    userAgent: row.user_agent,
    adminNote: row.admin_note,
    publicReply: row.public_reply
  };
}

function cleanText(value, maximumLength) {
  return String(value ?? '')
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '')
    .trim()
    .slice(0, maximumLength);
}

function cleanEnum(value, allowed, fallback) {
  return allowed.includes(value) ? value : fallback;
}

function makePublicFeedbackId(now) {
  const date = now.slice(0, 10).replaceAll('-', '');
  const random = crypto.randomUUID()
    .replaceAll('-', '')
    .slice(0, 8)
    .toUpperCase();
  return `FB-${date}-${random}`;
}

async function sha256Hex(value) {
  const digest = await crypto.subtle.digest(
    'SHA-256',
    new TextEncoder().encode(String(value))
  );
  return [...new Uint8Array(digest)]
    .map(byte => byte.toString(16).padStart(2, '0'))
    .join('');
}

async function proxyCatalog(request) {
  const response = await fetch(
    `https://raw.githubusercontent.com/${CONNECTOR_REPO}/main/catalog.json`,
    {
      method: request.method === 'HEAD' ? 'HEAD' : 'GET',
      headers: {
        Accept: 'application/json',
        'User-Agent': 'BiliNCM-Connector-Catalog/1.0'
      },
      cf: {
        cacheEverything: true,
        cacheTtl: 300
      }
    }
  );

  return copyProxyResponse(response, {
    contentType: 'application/json; charset=utf-8',
    cacheControl: 'public, max-age=300'
  });
}

async function proxyGitHub(request, target, options = {}) {
  let targetUrl;
  try {
    targetUrl = new URL(target);
  } catch {
    return jsonResponse({ error: 'Invalid target URL.' }, 400);
  }

  if (!isAllowedGitHubHost(targetUrl.hostname)) {
    return jsonResponse({ error: 'Access denied.' }, 403);
  }

  const headers = new Headers({
    Accept: request.headers.get('Accept') || '*/*',
    'User-Agent': 'BiliNCM-Cloudflare-Download-Proxy/1.0'
  });
  for (const name of [
    'Range',
    'If-None-Match',
    'If-Modified-Since'
  ]) {
    const value = request.headers.get(name);
    if (value) {
      headers.set(name, value);
    }
  }

  let response;
  try {
    response = await fetch(targetUrl.toString(), {
      method: request.method === 'HEAD' ? 'HEAD' : 'GET',
      headers,
      redirect: 'follow',
      cf: options.cacheControl
        ? {
            cacheEverything: true,
            cacheTtl: 31536000
          }
        : undefined
    });
  } catch (error) {
    return jsonResponse(
      {
        error: 'Proxy request failed.',
        details: String(error?.message || error)
      },
      502
    );
  }

  if (response.status === 404) {
    return jsonResponse(
      { error: 'GitHub Release asset was not found.' },
      404
    );
  }

  return copyProxyResponse(response, options);
}

function copyProxyResponse(response, options = {}) {
  const headers = new Headers(response.headers);
  for (const name of [
    'set-cookie',
    'content-security-policy',
    'content-security-policy-report-only'
  ]) {
    headers.delete(name);
  }

  for (const [name, value] of Object.entries(corsHeaders())) {
    headers.set(name, value);
  }
  if (options.downloadName) {
    headers.set(
      'Content-Disposition',
      `attachment; filename="${options.downloadName}"`
    );
  }
  if (options.contentType) {
    headers.set('Content-Type', options.contentType);
  }
  if (options.cacheControl) {
    headers.set('Cache-Control', options.cacheControl);
  }

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers
  });
}

function parseGenericProxyTarget(request, url) {
  let target = request.url.substring(
    request.url.indexOf(url.pathname) + 1
  );
  target = target.replace(/^https?:\/+/, 'https://');
  if (
    target.startsWith('github.com')
    || target.startsWith('raw.githubusercontent.com')
  ) {
    target = `https://${target}`;
  }

  if (!target.startsWith('https://')) {
    return null;
  }

  try {
    const parsed = new URL(target);
    return isAllowedGitHubHost(parsed.hostname)
      ? parsed.toString()
      : null;
  } catch {
    return null;
  }
}

async function proxyCoreAsset(request, project, assetName, options = {}) {
  if (
    !assetName
    || assetName.includes('/')
    || assetName.includes('\\')
    || assetName.includes('..')
  ) {
    return new Response('无效的发布文件名', { status: 400 });
  }

  let tag;
  try {
    tag = await resolveLatestChannelTag(
      project.repo,
      project.versionPrefix
    );
  } catch (error) {
    return jsonResponse(
      { error: String(error.message || error) },
      502
    );
  }

  return proxyGitHub(
    request,
    `https://github.com/${project.repo}/releases/download/`
      + `${encodeURIComponent(tag)}/${encodeURIComponent(assetName)}`,
    options
  );
}

async function resolveLatestChannelTag(repo, versionPrefix) {
  const cacheKey = new Request(
    `https://release-channel-cache.invalid/${repo}/${versionPrefix}`
  );
  const cache = caches.default;
  const cached = await cache.match(cacheKey);
  if (cached) {
    return cached.text();
  }

  const response = await fetch(
    `https://api.github.com/repos/${repo}/releases?per_page=50`,
    {
      headers: {
        Accept: 'application/vnd.github+json',
        'User-Agent': 'AwooMusicBot-Release-Channel/1.1'
      }
    }
  );
  if (!response.ok) {
    throw new Error(`GitHub Release 查询失败：HTTP ${response.status}`);
  }

  const releases = await response.json();
  const prefix = `v${versionPrefix}`;
  const matches = releases
    .filter(release =>
      !release.draft
      && !release.prerelease
      && String(release.tag_name || '').startsWith(prefix)
    )
    .sort((left, right) =>
      compareSemanticVersions(right.tag_name, left.tag_name)
    );
  if (matches.length === 0) {
    throw new Error(`没有找到 ${versionPrefix}x 发布版本`);
  }

  const tag = String(matches[0].tag_name);
  await cache.put(
    cacheKey,
    new Response(tag, {
      headers: {
        'Cache-Control': 'public, max-age=300'
      }
    })
  );
  return tag;
}

function compareSemanticVersions(left, right) {
  const parse = value => String(value)
    .replace(/^v/i, '')
    .split(/[.-]/)
    .map(part => Number.parseInt(part, 10) || 0);
  const a = parse(left);
  const b = parse(right);
  for (let index = 0; index < Math.max(a.length, b.length); index += 1) {
    const difference = (a[index] || 0) - (b[index] || 0);
    if (difference !== 0) return difference;
  }
  return 0;
}

function isAllowedGitHubHost(hostname) {
  for (const allowed of GITHUB_HOSTS) {
    if (hostname === allowed || hostname.endsWith(`.${allowed}`)) {
      return true;
    }
  }
  return false;
}

function corsHeaders() {
  return {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET, HEAD, POST, PATCH, OPTIONS',
    'Access-Control-Allow-Headers':
      'Range, If-None-Match, If-Modified-Since, Content-Type, Authorization',
    'Access-Control-Expose-Headers':
      'Content-Length, Content-Range, ETag, Last-Modified'
  };
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value, null, 2), {
    status,
    headers: {
      ...corsHeaders(),
      'Content-Type': 'application/json; charset=utf-8'
    }
  });
}

function htmlResponse(html) {
  return new Response(html, {
    headers: {
      'Content-Type': 'text/html; charset=utf-8'
    }
  });
}

function renderHome(host) {
  const cards = Object.entries(CORE_PROJECTS)
    .map(([id, project]) => `
      <section class="card">
        ${project.badge ? `<span class="badge${project.featured ? ' featured' : ''}">${escapeHtml(project.badge)}</span>` : ''}
        <h2>${escapeHtml(project.name)}</h2>
        <p>${escapeHtml(project.description)}</p>
        <div class="actions">
          <a class="primary" href="/download/${id}">本站下载</a>
          <a href="https://github.com/${project.repo}">GitHub</a>
        </div>
      </section>
    `)
    .join('');

  return `<!doctype html>
  <html lang="zh-CN">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width,initial-scale=1">
    <title>Enkianssus App Hub</title>
    <style>
      :root{color-scheme:dark;--bg:#0d1117;--card:#161b22;--border:#30363d;--text:#c9d1d9;--muted:#8b949e;--green:#238636}
      *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}
      main{width:min(720px,calc(100% - 32px));margin:48px auto}.panel{background:var(--card);border:1px solid var(--border);border-radius:18px;padding:32px;box-shadow:0 20px 50px #0008}
      h1{margin:0 0 8px;color:#fff}.status{color:#3fb950;font-size:13px}.card{margin-top:28px;padding-top:24px;border-top:1px solid var(--border)}
      h2{font-size:18px;color:#fff}p{line-height:1.7;color:var(--muted)}.actions{display:flex;gap:12px;flex-wrap:wrap}.badge{display:inline-block;padding:4px 9px;border-radius:999px;background:#21262d;border:1px solid var(--border);color:#8b949e;font-size:12px;font-weight:700}.badge.featured{background:#23863633;border-color:#3fb95066;color:#56d364}
      a{color:var(--text);text-decoration:none;background:#21262d;border:1px solid var(--border);border-radius:8px;padding:11px 18px;font-weight:650}
      a.primary{background:var(--green);color:#fff}.endpoint{font-family:ui-monospace,Consolas,monospace;background:#010409;border:1px solid var(--border);padding:12px;border-radius:8px;overflow:auto}
      footer{margin-top:30px;color:#484f58;font-size:12px;text-align:center}
    </style>
  </head>
  <body>
    <main><div class="panel">
      <h1>Enkianssus App Hub</h1>
      <div class="status">● Cloudflare 分发节点运行中</div>
      ${cards}
      <section class="card">
        <h2>嗷呜点歌机播放器连接器</h2>
        <p>网易云音乐、酷狗音乐、QQ 音乐和 Folia 连接器独立更新，不需要同步升级嗷呜点歌机本体。</p>
        <div class="endpoint">https://${host}/connectors/v1/catalog.json</div>
        <div class="actions" style="margin-top:16px">
          <a href="/connectors/v1/catalog.json">查看版本清单</a>
          <a href="https://github.com/${CONNECTOR_REPO}">连接器源码</a>
          <a href="/feedback">提交问题反馈</a>
        </div>
      </section>
      <footer>© Enkianssus · enkianss.us</footer>
    </div></main>
  </body>
  </html>`;
}

function renderFeedback() {
  return `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>问题反馈 · Enkianssus App Hub</title><style>
:root{color-scheme:dark;--bg:#0d1117;--card:#161b22;--line:#30363d;--text:#e6edf3;--muted:#8b949e}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.6 system-ui,"Segoe UI",sans-serif}main{width:min(820px,calc(100% - 28px));margin:32px auto}.card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:26px;margin-bottom:16px}h1,h2{margin:0 0 8px}p{color:var(--muted)}label{display:block;font-weight:650;margin:16px 0 6px}input,select,textarea{width:100%;background:#0d1117;color:var(--text);border:1px solid var(--line);border-radius:9px;padding:11px;font:inherit}textarea{min-height:160px;resize:vertical}.grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}.actions{display:flex;gap:12px;margin-top:20px;flex-wrap:wrap}button,a.btn{border:0;border-radius:9px;padding:11px 18px;font-weight:700;cursor:pointer;text-decoration:none;background:#21262d;color:var(--text)}button.primary{background:#1f6feb;color:#fff}button:disabled{opacity:.55}.message{padding:13px;border-radius:9px;margin-top:16px;white-space:pre-wrap}.ok{background:#23863633;border:1px solid #3fb95066}.err{background:#da363333;border:1px solid #f8514966}.hidden{display:none}@media(max-width:640px){.grid{grid-template-columns:1fr}.card{padding:18px}}
</style></head><body><main>
<section class="card"><h1>问题反馈与兼容性报告</h1><p>可提交播放器连接、连接器兼容、点歌流程或功能建议。点歌机内提交时会在你确认后附带版本诊断，不会上传登录 Cookie、二维码凭据或用户白名单。</p></section>
<section class="card"><form id="feedbackForm">
<div class="grid"><div><label>类型</label><select name="category"><option value="bug">软件问题</option><option value="connector">连接器问题</option><option value="compatibility">播放器兼容性</option><option value="feature">功能建议</option><option value="other">其他</option></select></div><div><label>影响程度</label><select name="priority"><option value="normal">一般</option><option value="high">严重影响使用</option><option value="critical">完全无法使用</option><option value="low">轻微</option></select></div></div>
<label>标题</label><input name="title" maxlength="120" required placeholder="例如：网易云更新后无法插入下一首">
<label>详细描述</label><textarea name="description" maxlength="8000" required placeholder="请写清复现步骤、预期结果和实际结果"></textarea>
<div class="grid"><div><label>播放器</label><select name="selectedPlayer"><option value="">未指定</option><option value="netease">网易云音乐</option><option value="kugou">酷狗音乐</option><option value="qqmusic">QQ 音乐</option><option value="folia">Folia</option></select></div><div><label>播放器版本</label><input name="playerVersion" maxlength="80" placeholder="例如 3.1.37.205354"></div></div>
<label>联系方式（可选）</label><input name="contact" maxlength="200" placeholder="邮箱、GitHub 或其他联系方式">
<div class="actions"><button class="primary" id="submitButton" type="submit">提交反馈</button><a class="btn" href="/">返回下载页</a></div></form>
<div id="message" class="message hidden"></div></section>
<section class="card hidden" id="trackingCard"><h2>查询反馈</h2><p id="trackingText"></p></section>
</main><script>
const form=document.getElementById('feedbackForm'),message=document.getElementById('message'),button=document.getElementById('submitButton');
function show(text,ok){message.textContent=text;message.className='message '+(ok?'ok':'err')}
form.addEventListener('submit',async event=>{event.preventDefault();button.disabled=true;button.textContent='提交中…';try{const data=Object.fromEntries(new FormData(form));data.source='web';const response=await fetch('/api/v1/feedback',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});const result=await response.json();if(!response.ok)throw new Error(result.error||'提交失败');show('提交成功。反馈编号：'+result.id+'\\n请保存这个编号。',true);history.replaceState(null,'','/feedback?id='+encodeURIComponent(result.id));loadTracking(result.id)}catch(error){show(error.message||String(error),false)}finally{button.disabled=false;button.textContent='提交反馈'}});
async function loadTracking(id){if(!id)return;const card=document.getElementById('trackingCard'),text=document.getElementById('trackingText');card.classList.remove('hidden');text.textContent='正在查询 '+id+'…';try{const response=await fetch('/api/v1/feedback/'+encodeURIComponent(id));const result=await response.json();if(!response.ok)throw new Error(result.error||'查询失败');text.textContent='编号：'+result.id+'\\n状态：'+result.status+'\\n标题：'+result.title+(result.reply?'\\n处理回复：'+result.reply:'')}catch(error){text.textContent=error.message||String(error)}}
loadTracking(new URLSearchParams(location.search).get('id'));
</script></body></html>`;
}

function renderFeedbackAdmin() {
  return `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>反馈处理后台</title><style>
:root{color-scheme:dark;--bg:#0d1117;--card:#161b22;--line:#30363d;--text:#e6edf3;--muted:#8b949e}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.55 system-ui,"Segoe UI",sans-serif}header{position:sticky;top:0;z-index:2;background:#0d1117ee;border-bottom:1px solid var(--line);padding:14px}.bar{max-width:1280px;margin:auto;display:flex;gap:10px;align-items:center;flex-wrap:wrap}main{max-width:1280px;margin:20px auto;padding:0 14px;display:grid;grid-template-columns:minmax(340px,42%) 1fr;gap:16px}.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px}input,select,textarea,button{background:#0d1117;color:var(--text);border:1px solid var(--line);border-radius:8px;padding:9px;font:inherit}input{min-width:180px}button{cursor:pointer;font-weight:700}.primary{background:#1f6feb}.item{padding:12px;border:1px solid var(--line);border-radius:9px;margin:9px 0;cursor:pointer}.item:hover,.item.active{border-color:#58a6ff;background:#1f6feb18}.meta{color:var(--muted);font-size:12px}.pill{display:inline-block;padding:2px 7px;border:1px solid var(--line);border-radius:999px;margin-right:5px}label{display:block;margin:12px 0 5px;font-weight:650}textarea{width:100%;min-height:100px;resize:vertical}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#010409;padding:12px;border-radius:8px;max-height:360px;overflow:auto}.empty{color:var(--muted);padding:30px;text-align:center}@media(max-width:800px){main{grid-template-columns:1fr}}
</style></head><body><header><div class="bar"><strong>反馈处理后台</strong><input id="token" type="password" placeholder="管理令牌（仅本次会话保存）"><select id="statusFilter"><option value="">全部状态</option><option>open</option><option>triaging</option><option>working</option><option>resolved</option><option>closed</option><option>duplicate</option></select><input id="query" placeholder="搜索编号、标题或描述"><button class="primary" id="load">加载</button><span id="notice" class="meta"></span></div></header>
<main><section class="card"><div id="list" class="empty">输入管理令牌后加载反馈</div></section><section class="card"><div id="detail" class="empty">选择一条反馈查看详情</div></section></main><script>
const token=document.getElementById('token'),list=document.getElementById('list'),detail=document.getElementById('detail'),notice=document.getElementById('notice');let selected=null;token.value=sessionStorage.getItem('feedbackAdminToken')||'';
function esc(value){return String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]))}
async function api(path,options={}){const value=token.value.trim();sessionStorage.setItem('feedbackAdminToken',value);const response=await fetch(path,{...options,headers:{'Content-Type':'application/json','Authorization':'Bearer '+value,...options.headers}});const result=await response.json();if(!response.ok)throw new Error(result.error||'请求失败');return result}
async function load(){notice.textContent='加载中…';try{const params=new URLSearchParams(),status=document.getElementById('statusFilter').value,q=document.getElementById('query').value.trim();if(status)params.set('status',status);if(q)params.set('q',q);const result=await api('/api/v1/admin/feedback?'+params);renderList(result.items);notice.textContent='共 '+result.items.length+' 条'}catch(error){notice.textContent=error.message||String(error)}}
function renderList(items){list.className='';list.innerHTML=items.length?items.map(item=>'<div class="item" data-id="'+esc(item.id)+'"><div><span class="pill">'+esc(item.status)+'</span><span class="pill">'+esc(item.priority)+'</span>'+esc(item.publicId)+'</div><strong>'+esc(item.title)+'</strong><div class="meta">'+esc(item.selectedPlayer||'未指定播放器')+' · '+esc(item.playerVersion||'未知版本')+' · '+esc(item.createdAt)+'</div></div>').join(''):'<div class="empty">没有匹配的反馈</div>';list.querySelectorAll('.item').forEach(node=>node.onclick=()=>select(items.find(item=>item.id===node.dataset.id)))}
function select(item){selected=item;document.querySelectorAll('.item').forEach(node=>node.classList.toggle('active',node.dataset.id===item.id));detail.className='';detail.innerHTML='<h2>'+esc(item.title)+'</h2><div class="meta">'+esc(item.publicId)+' · '+esc(item.category)+' · '+esc(item.createdAt)+'</div><p>'+esc(item.description).replace(/\\n/g,'<br>')+'</p><h3>环境</h3><pre>'+esc(JSON.stringify({appVersion:item.appVersion,coreVersion:item.coreVersion,platform:item.platform,architecture:item.architecture,osVersion:item.osVersion,selectedPlayer:item.selectedPlayer,playerVersion:item.playerVersion,connectorId:item.connectorId,connectorVersion:item.connectorVersion,latestConnectorVersion:item.latestConnectorVersion,connectionStatus:item.connectionStatus,contact:item.contact,country:item.country},null,2))+'</pre><h3>诊断信息</h3><pre>'+esc(JSON.stringify(item.diagnostics,null,2))+'</pre><label>状态</label><select id="editStatus">'+['open','triaging','working','resolved','closed','duplicate'].map(v=>'<option '+(v===item.status?'selected':'')+'>'+v+'</option>').join('')+'</select><label>优先级</label><select id="editPriority">'+['low','normal','high','critical'].map(v=>'<option '+(v===item.priority?'selected':'')+'>'+v+'</option>').join('')+'</select><label>内部备注</label><textarea id="adminNote">'+esc(item.adminNote)+'</textarea><label>公开回复</label><textarea id="publicReply">'+esc(item.publicReply)+'</textarea><p><button class="primary" id="save">保存处理结果</button></p>';document.getElementById('save').onclick=save}
async function save(){if(!selected)return;notice.textContent='保存中…';try{const result=await api('/api/v1/admin/feedback/'+selected.id,{method:'PATCH',body:JSON.stringify({status:document.getElementById('editStatus').value,priority:document.getElementById('editPriority').value,adminNote:document.getElementById('adminNote').value,publicReply:document.getElementById('publicReply').value})});selected=result.item;select(selected);notice.textContent='已保存';await load()}catch(error){notice.textContent=error.message||String(error)}}
document.getElementById('load').onclick=load;document.getElementById('query').addEventListener('keydown',event=>{if(event.key==='Enter')load()});if(token.value)load();
</script></body></html>`;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}
