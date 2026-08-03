const COVER_PATH =
  /^\/([A-Za-z0-9_-]{16,64}={0,2})\/([0-9]{1,20})\.jpg$/;

export default {
  async fetch(request) {
    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders() });
    }
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return jsonResponse({ error: 'Method not allowed.' }, 405);
    }

    const url = new URL(request.url);
    const match = url.pathname.match(COVER_PATH);
    if (!match) {
      return jsonResponse({ error: 'Invalid NetEase cover path.' }, 404);
    }

    const [, token, picId] = match;
    const upstream =
      `https://p1.music.126.net/${token}/${picId}.jpg?param=600y600`;
    let response;
    try {
      response = await fetch(upstream, {
        method: 'GET',
        headers: {
          Accept: 'image/avif,image/webp,image/apng,image/*,*/*;q=0.8',
          Referer: 'https://music.163.com/',
          'User-Agent': 'AwooMusicBot-Netease-Cover-Proxy/1.0'
        },
        cf: {
          cacheEverything: true,
          cacheTtl: 604800
        }
      });
    } catch (error) {
      return jsonResponse(
        {
          error: 'NetEase cover request failed.',
          details: String(error?.message || error)
        },
        502
      );
    }

    if (!response.ok) {
      return jsonResponse(
        { error: `NetEase cover returned HTTP ${response.status}.` },
        response.status
      );
    }

    const headers = new Headers(response.headers);
    headers.delete('set-cookie');
    headers.set('Access-Control-Allow-Origin', '*');
    headers.set('Cache-Control', 'public, max-age=604800, immutable');
    headers.set(
      'Content-Type',
      response.headers.get('Content-Type') || 'image/jpeg'
    );
    return new Response(request.method === 'HEAD' ? null : response.body, {
      status: response.status,
      statusText: response.statusText,
      headers
    });
  }
};

function jsonResponse(body, status) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      ...corsHeaders(),
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store'
    }
  });
}

function corsHeaders() {
  return {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type'
  };
}
