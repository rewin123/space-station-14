import type {
  AgentCommand,
  AgentCommandResult,
  AgentEventsResponse,
  AgentHealth,
  AgentSessionSnapshot,
  AgentStateSnapshot,
} from './types'

/**
 * An error that knows what it was.
 *
 * Distinguishing them matters: retrying a 401 is pointless (the token won't become valid on its
 * own), same for a 400 (that's a client bug), while a network drop or a 5xx is exactly what
 * backoff exists for. A client without this branch hammers the server forever with a bad token
 * and looks like it "just doesn't work".
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }

  /** No point retrying: the response won't change until the request or the token does. */
  get terminal(): boolean {
    return this.status === 401 || this.status === 400 || this.status === 404
  }
}

/**
 * How long we wait for a response before deciding the server is dead.
 *
 * STRICTLY greater than the server's PollTimeout (25s), and that's not a margin for safety's
 * sake. `AgentDebugServer.HandleAsync` passes the router a SERVER SHUTDOWN token, not a request
 * one, and `HttpListener` doesn't report client disconnects. An aborted request stays parked
 * inside `ReadAsync` for all the remaining seconds and keeps holding one of the sixteen slots.
 * A two-second timeout would mean thirteen occupied slots per tab, and once they're exhausted the
 * accept loop itself locks up: /state and /command stop responding for everyone.
 */
const DEAD_SERVER_MS = 35_000

export interface Endpoint {
  baseUrl: string
  token: string
}

async function request<T>(
  endpoint: Endpoint,
  path: string,
  init: RequestInit,
  signal?: AbortSignal,
): Promise<T> {
  const timer = new AbortController()
  const timeout = setTimeout(() => timer.abort(), DEAD_SERVER_MS)

  // The external signal (unmount, generation change) and our own timeout both abort the request.
  const signals = signal ? AbortSignal.any([signal, timer.signal]) : timer.signal

  try {
    const response = await fetch(endpoint.baseUrl.replace(/\/$/, '') + path, {
      ...init,
      signal: signals,
      headers: {
        // An empty token means we don't send the header at all. Behind a reverse proxy the
        // proxy supplies it itself, and the page doesn't know about it; sending `Bearer ` with
        // an empty string would just be junk in the request.
        ...(endpoint.token ? { Authorization: `Bearer ${endpoint.token}` } : {}),
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        ...init.headers,
      },
      // 'same-origin' — and exactly that, not 'omit' and not 'include'.
      //
      // 'include' would break development: Allow-Credentials is incompatible with the `*` in
      // Allow-Origin that the debug server sets, and every cross-origin request would fail with
      // a cryptic wildcard error.
      //
      // 'omit' breaks prod, and that was written here from experience. Behind a reverse proxy
      // the page and the API sit on the same origin, but access is gated by basic-auth; the
      // browser would attach the credentials itself, but 'omit' strips them — and the request
      // never reaches the game server at all. From the outside this looks like "wrong token",
      // even though it was the proxy that returned the 401.
      //
      // 'same-origin' gives both behaviors at once: its own origin gets credentials, another
      // origin doesn't.
      credentials: 'same-origin',

      // Belt and suspenders: the server sends no-store, but /events is a repeating GET with the
      // same URL as long as the cursor hasn't moved, and the cost of a mistake here is a frozen
      // UI. Let the browser not even have the theoretical possibility of answering from cache.
      cache: 'no-store',
    })

    const text = await response.text()

    if (!response.ok) {
      let detail: string
      try {
        detail = (JSON.parse(text) as { error?: string }).error ?? text
      } catch {
        // Not JSON — meaning it wasn't our server that answered, but something along the way
        // (a reverse proxy, a gateway). Dumping its HTML page into the UI is pointless: it's a
        // hundred lines of markup and not a word about the cause. Saying WHO failed is the
        // useful part.
        detail = `${response.statusText || 'ошибка'} — ответил не отладочный сервер, а посредник`
      }
      throw new ApiError(response.status, detail || response.statusText)
    }

    return JSON.parse(text) as T
  } finally {
    clearTimeout(timeout)
  }
}

export function getHealth(endpoint: Endpoint, signal?: AbortSignal): Promise<AgentHealth> {
  return request<AgentHealth>(endpoint, '/health', { method: 'GET' }, signal)
}

export function getState(endpoint: Endpoint, signal?: AbortSignal): Promise<AgentStateSnapshot> {
  return request<AgentStateSnapshot>(endpoint, '/state', { method: 'GET' }, signal)
}

/**
 * A single agent's snapshot.
 *
 * An unknown agent comes back as `{agent: null}` with status 200 — that must be handled as
 * "the slice went empty", not as an error: a 404 here would be terminal and would stop the loop
 * forever.
 */
export function getSession(
  endpoint: Endpoint,
  agent: string,
  signal?: AbortSignal,
): Promise<AgentSessionSnapshot> {
  const query = new URLSearchParams({ agent })
  return request<AgentSessionSnapshot>(endpoint, `/session?${query}`, { method: 'GET' }, signal)
}

/**
 * Long polling: the server holds the response for up to 25 seconds if nothing happened.
 *
 * An empty list once it times out is a normal response, not a degradation.
 */
export function getEvents(
  endpoint: Endpoint,
  instance: string,
  since: number,
  signal?: AbortSignal,
): Promise<AgentEventsResponse> {
  const query = new URLSearchParams({ since: String(since), instance })
  return request<AgentEventsResponse>(endpoint, `/events?${query}`, { method: 'GET' }, signal)
}

/**
 * A command to the agent.
 *
 * The caller must be single-flight and NEVER retry it automatically: `AgentInbox.Enqueue` glues
 * two messages together with a newline instead of rejecting the second one, and there is no
 * idempotency key on the wire. Retrying a request that already succeeded produces one message
 * with the text doubled.
 */
export function postCommand(
  endpoint: Endpoint,
  command: AgentCommand,
  signal?: AbortSignal,
): Promise<AgentCommandResult> {
  return request<AgentCommandResult>(
    endpoint,
    '/command',
    { method: 'POST', body: JSON.stringify(command) },
    signal,
  )
}
