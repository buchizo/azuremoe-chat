const { app } = require('@azure/functions');

const SKIP_HEADERS = new Set([
    'content-encoding',
    'content-security-policy',
    'transfer-encoding',
    'connection',
]);

app.http('hf', {
    methods: ['GET', 'HEAD'],
    authLevel: 'anonymous',
    route: 'hf/{*path}',
    handler: async (request, context) => {
        try {
            const hfPath = request.params.path ?? '';

            let search = '';
            try { search = new URL(request.url).search; } catch { /* relative URL */ }

            const target = `https://huggingface.co/${hfPath}${search}`;
            context.log(`HF proxy: ${request.method} ${target}`);

            const init = { method: request.method, headers: {}, redirect: 'follow' };
            const range = request.headers.get('Range');
            if (range) init.headers['Range'] = range;

            const upstream = await fetch(target, init);
            context.log(`HF upstream status: ${upstream.status}`);

            const responseHeaders = {
                'access-control-allow-origin': '*',
                'cross-origin-resource-policy': 'cross-origin',
            };
            for (const [key, value] of upstream.headers) {
                if (!SKIP_HEADERS.has(key.toLowerCase())) {
                    responseHeaders[key] = value;
                }
            }

            // Buffer the body (streaming ReadableStream is not reliably supported
            // in SWA managed functions). Range requests keep each chunk small so
            // memory pressure is bounded even for large model files.
            const body = request.method === 'HEAD'
                ? undefined
                : Buffer.from(await upstream.arrayBuffer());

            return { status: upstream.status, headers: responseHeaders, body };

        } catch (err) {
            context.error('HF proxy error:', err);
            return {
                status: 502,
                headers: { 'content-type': 'text/plain' },
                body: `HF proxy error: ${err.message}`,
            };
        }
    },
});
