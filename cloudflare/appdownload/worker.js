const CORE_PROJECTS = {
  bilincm: {
    name: 'BiliNCM 点歌机',
    repo: 'Enkianssus/BiliNCM-TS',
    exeName: 'bilincm-win-Portable.zip',
    description: 'B 站弹幕点歌和多播放器控制客户端。'
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
  async fetch(request) {
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

    if (url.pathname === '/connectors/v1/catalog.json') {
      return proxyCatalog(request);
    }

    const connectorDownload = url.pathname.match(
      /^\/connectors\/v1\/download\/(netease|kugou|qqmusic|folia)\/([0-9]+\.[0-9]+\.[0-9]+)\/([^/]+)$/
    );
    if (connectorDownload) {
      const [, connectorId, version, assetName] = connectorDownload;
      const expectedAsset =
        `bilincm-connector-${connectorId}-${version}-win-x86.zip`;
      if (!CONNECTOR_IDS.has(connectorId) || assetName !== expectedAsset) {
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

      return proxyGitHub(
        request,
        `https://github.com/${project.repo}/releases/latest/download/`
          + encodeURIComponent(project.exeName),
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

      return proxyGitHub(
        request,
        `https://github.com/${project.repo}/releases/latest/download/`
          + coreUpdate[2]
      );
    }

    const genericTarget = parseGenericProxyTarget(request, url);
    if (genericTarget) {
      return proxyGitHub(request, genericTarget);
    }

    return htmlResponse(renderHome(url.host));
  }
};

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
    'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
    'Access-Control-Allow-Headers':
      'Range, If-None-Match, If-Modified-Since',
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
      h2{font-size:18px;color:#fff}p{line-height:1.7;color:var(--muted)}.actions{display:flex;gap:12px;flex-wrap:wrap}
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
        <h2>BiliNCM 播放器连接器</h2>
        <p>网易云音乐、酷狗音乐和 QQ 音乐连接器独立更新，不需要同步升级 BiliNCM 本体。</p>
        <div class="endpoint">https://${host}/connectors/v1/catalog.json</div>
        <div class="actions" style="margin-top:16px">
          <a href="/connectors/v1/catalog.json">查看版本清单</a>
          <a href="https://github.com/${CONNECTOR_REPO}">连接器源码</a>
        </div>
      </section>
      <footer>© Enkianssus · enkianss.us</footer>
    </div></main>
  </body>
  </html>`;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}
