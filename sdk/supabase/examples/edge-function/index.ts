/**
 * Minimal Supabase Edge Function that reports unhandled exceptions to
 * glitch-port.
 *
 * Deploy it from the repository root (the directory that contains
 * `supabase/functions/hello/index.ts`) with:
 *
 *     supabase secrets set GLITCHPORT_DSN='https://<publicKey>@<host>/<projectId>'
 *     supabase functions deploy hello
 *
 * A normal request returns `ok`:
 *
 *     curl https://<project>.supabase.co/functions/v1/hello
 *
 * A request with `?fail=1` throws on purpose; `withGlitchPort` captures the
 * exception and replies with the standard 500 JSON body that carries the
 * reported `event_id`:
 *
 *     curl "https://<project>.supabase.co/functions/v1/hello?fail=1"
 *
 * Copy `sdk/supabase/src` next to this file (see `documents/sdk-supabase.md`)
 * or change the import below to the raw GitHub URL of `glitchport.ts`.
 * In a Supabase project the file lives at
 * `supabase/functions/hello/index.ts` and the vendored source is copied into
 * `supabase/functions/hello/`, so the import becomes `./glitchport.ts`.
 */

import * as GlitchPort from '../../src/glitchport.ts';

GlitchPort.init({
  dsn: Deno.env.get('GLITCHPORT_DSN')!,
  environment: 'production',
  release: 'api@1.2.0',
});

Deno.serve(
  GlitchPort.withGlitchPort(async (req) => {
    const url = new URL(req.url);

    // Exercise the automatic error path: capture, flush and 500 response.
    if (url.searchParams.get('fail') === '1') {
      throw new Error('intentional failure from supabase edge function');
    }

    return new Response('ok');
  }),
);
