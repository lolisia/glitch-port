// Minimal ambient declaration of the Deno global used by the Supabase Edge
// Function example. The SDK runtime never calls the Deno global; this file
// only lets the example typecheck without pulling in @types/deno.
declare const Deno: {
  env: {
    get(name: string): string | undefined;
  };
  serve(handler: (request: Request, info?: unknown) => Response | Promise<Response>): unknown;
  version?: {
    deno?: string;
  };
};
