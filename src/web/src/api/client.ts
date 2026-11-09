// Thin fetch wrapper. Same-origin cookie session; CSRF token echoed on unsafe methods; RFC 9457 problems surfaced as ApiError.

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string | undefined,
    message: string,
    public readonly body: unknown,
    public readonly fields?: Record<string, string[]>,
  ) {
    super(message);
  }
}

let csrfToken: string | null = null;

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
}

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

interface RequestOptions {
  body?: unknown;
  idempotencyKey?: string;
  ifMatch?: string;
}

export interface ApiResponse<T> {
  data: T;
  etag: string | null;
  headers: Headers;
}

async function request<T>(method: string, url: string, opts: RequestOptions = {}): Promise<ApiResponse<T>> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (opts.body !== undefined) headers["Content-Type"] = "application/json";
  if (method !== "GET" && csrfToken) headers["X-CSRF-Token"] = csrfToken;
  if (opts.idempotencyKey) headers["Idempotency-Key"] = opts.idempotencyKey;
  if (opts.ifMatch) headers["If-Match"] = opts.ifMatch;
  const res = await fetch(url, {
    method,
    headers,
    credentials: "same-origin",
    body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
  });
  const text = await res.text();
  const json: unknown = text ? safeParse(text) : null;
  if (!res.ok && res.status !== 409) {
    throw toError(res.status, json);
  }
  if (res.status === 409 && !isDuplicateReview(json)) {
    throw toError(res.status, json);
  }
  return { data: json as T, etag: res.headers.get("ETag"), headers: res.headers };
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function isDuplicateReview(json: unknown): boolean {
  return typeof json === "object" && json !== null && (json as { outcome?: string }).outcome === "DuplicateReviewRequired";
}

function toError(status: number, json: unknown): ApiError {
  const p = (typeof json === "object" && json !== null ? json : {}) as { code?: string; detail?: string; title?: string; errors?: Record<string, string[]> };
  const message = p.detail ?? p.title ?? (status === 401 ? "Your session has ended. Please sign in again." : `Request failed (${status}).`);
  return new ApiError(status, p.code, message, json, p.errors);
}

export const api = {
  get: <T>(url: string) => request<T>("GET", url),
  post: <T>(url: string, body?: unknown, opts: Omit<RequestOptions, "body"> = {}) => request<T>("POST", url, { ...opts, body }),
  put: <T>(url: string, body: unknown, opts: Omit<RequestOptions, "body"> = {}) => request<T>("PUT", url, { ...opts, body }),
};

/** Multipart upload of a single file in the "file" field (CSRF header included). */
export async function uploadFile<T>(url: string, file: File): Promise<T> {
  const form = new FormData();
  form.append("file", file);
  const headers: Record<string, string> = { Accept: "application/json" };
  if (csrfToken) headers["X-CSRF-Token"] = csrfToken;
  const res = await fetch(url, { method: "POST", body: form, headers, credentials: "same-origin" });
  const text = await res.text();
  const json: unknown = text ? safeParse(text) : null;
  if (!res.ok) throw toError(res.status, json);
  return json as T;
}

/** Fetches a PDF as a blob URL (authenticated, never a public URL). Caller must revoke it. */
export async function fetchPdfUrl(url: string): Promise<{ url: string; snapshotSha256: string | null }> {
  const res = await fetch(url, { credentials: "same-origin" });
  if (!res.ok) {
    const text = await res.text();
    throw toError(res.status, text ? safeParse(text) : null);
  }
  const blob = await res.blob();
  return { url: URL.createObjectURL(blob), snapshotSha256: res.headers.get("X-Snapshot-Sha256") };
}
