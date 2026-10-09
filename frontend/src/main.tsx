import React, { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Activity,
  ArrowDownToLine,
  ArrowRight,
  Bell,
  Bot,
  Check,
  CheckCircle2,
  ChevronRight,
  CircleDot,
  Clock3,
  Cpu,
  FlaskConical,
  Gauge,
  Layers3,
  Play,
  Radio,
  Repeat2,
  Search,
  Settings2,
  ShieldCheck,
  Square,
  Terminal,
  TriangleAlert,
  X,
} from "lucide-react";
import "./style.css";
import { AgentsView, AISettingsForm, type AISettings, type AgentState } from "./agents";
import { AIUsageView } from "./usage";
import { IncidentWorkspace, eventDate } from "./investigation";

type Page =
  | "investigation"
  | "usage"
  | "overview"
  | "tester"
  | "incidents"
  | "events"
  | "agents"
  | "inbox"
  | "settings";
type TelemetryEvent = {
  id: string;
  ts: number;
  kind: string;
  service: string;
  path: string;
  status: number;
  duration_ms: number;
  cpu_percent: number | null;
  memory_mb: number | null;
  scope: string;
  message: string;
  request_id: string;
  command_signature: string;
};
type Notice = {
  id: string;
  ts: number;
  kind: string;
  incident_id: string;
  channel: string;
  body: { title: string; severity: string; message: string };
};
type Incident = {
  id: string;
  title: string;
  severity: string;
  category: string;
  rule: string;
  status: string;
  subject: string;
  opened: number;
  updated: number;
  closed: number | null;
  acknowledged: boolean;
  evidence: {
    value: number;
    threshold: number;
    detail: string;
    evidence_ids: string[];
  };
  report: { summary: string; origin: string };
  events?: TelemetryEvent[];
  notices?: Notice[];
};
type Overview = {
  health: string;
  error: string | null;
  last_cycle: number | null;
  source_lag: number | null;
  backend: string;
  data_state: "live" | "waiting" | "stale" | "unavailable";
  data_origin: string;
  measurement_scope: string;
  request_count: number | null;
  request_window_seconds: number;
  total_events: number;
  pending_events: number;
  requests_per_second: number | null;
  error_rate: number | null;
  resources: TelemetryEvent | null;
  active_incidents: number;
  histogram: { ts: number; observed: boolean; requests: number | null; errors: number | null }[];
  incidents: Incident[];
  activity: { id: string; ts: number; type: string; message: string }[];
};
type Scenario = {
  id: string;
  name: string;
  category: string;
  description: string;
  expected: string[];
};
type Run = {
  incidents?: { id: string; title: string; severity: string; status: string }[];
  id: string;
  scenario: string;
  status: string;
  started: number;
  ended: number | null;
  config: { duration: number; rps: number; clients: number; warmup: number };
  result: {
    sent: number;
    responses?: number;
    http_errors: number;
    transport_errors: number;
    phase: string;
    expected?: string[];
    detected?: string[];
    missing?: string[];
    additional?: string[];
    unresolved?: string[];
    recovery_confirmed?: boolean;
    evaluation?: string;
    error?: string;
  };
};
type AI = AISettings;
type SharedSession = { access: "viewer" | "master"; expires_at: string; csrf_token: string };
type SimulationLimits = { simulations: { daily_limit: number | null; used: number; remaining: number | null; resets_at: string | null; unlimited?: boolean } };
let sharedSession: SharedSession | null = null;
let privateConsole = false;

const pages: { id: Page; label: string; icon: typeof Activity }[] = [
  { id: "overview", label: "Overview", icon: Gauge },
  { id: "tester", label: "Scenario tester", icon: FlaskConical },
  { id: "incidents", label: "Incidents", icon: ShieldCheck },
  { id: "events", label: "Events", icon: Layers3 },
  { id: "agents", label: "Agents", icon: Bot },
  { id: "usage", label: "AI usage", icon: Activity },
  { id: "inbox", label: "Notifications", icon: Bell },
  { id: "settings", label: "Settings", icon: Settings2 },
];
const labels: Record<string, string> = {
  security: "Security",
  behavior: "Behavior",
  application: "Application",
  resources: "Resources",
  monitoring: "Monitoring",
  active: "Active",
  recovering: "Recovering",
  resolved: "Resolved",
  high: "High",
  warning: "Warning",
  healthy: "Healthy",
  degraded: "Degraded",
  waiting: "Waiting",
  running: "Running",
  settling: "Settling",
  completed: "Completed",
  stopped: "Stopped",
  failed: "Failed",
  interrupted: "Interrupted",
  warmup: "Normal traffic",
  anomaly: "Fault active",
  recovery: "Recovery",
  finished: "Finished",
};
const label = (s: string) => labels[s] || s;
const clock = (ts: number | null | undefined) =>
  ts
    ? new Date(ts * 1000).toLocaleTimeString("en-GB", {
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
      })
    : "—";
const short = (id: string) => id.slice(0, 8).toUpperCase();
const number = (n: number | undefined | null) =>
  n == null ? "—" : n.toLocaleString("en-GB", { maximumFractionDigits: 1 });
async function api<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch("/api" + url, {
    ...options,
    headers: { "Content-Type": "application/json", ...(sharedSession ? { "X-IncidentLab-CSRF": sharedSession.csrf_token } : {}), ...options?.headers },
  });
  if (response.status === 401 && response.headers.get("X-IncidentLab-Login") === "required") window.location.assign("/login");
  if (!response.ok) {
    const body = await response
      .json()
      .catch(() => ({ detail: response.statusText }));
    throw new Error(
      typeof body.detail === "string"
        ? body.detail
        : JSON.stringify(body.detail),
    );
  }
  return response.json();
}
function Badge({ value }: { value: string }) {
  return (
    <span className={"badge " + value}>
      <span />
      {label(value)}
    </span>
  );
}
function Empty({
  icon: Icon = CheckCircle2,
  title,
  detail,
}: {
  icon?: typeof Activity;
  title: string;
  detail: string;
}) {
  return (
    <div className="empty">
      <Icon size={28} />
      <strong>{title}</strong>
      <p>{detail}</p>
    </div>
  );
}
function Traffic({ data }: { data: Overview["histogram"] }) {
  if (!data.length) return <Empty icon={Radio} title="No live telemetry" detail="The chart requires observed events. Missing data is not zero traffic." />;
  const max = Math.max(10, ...data.map((x) => x.requests ?? 0));
  return (
    <div className="traffic">
      <div className="axis">
        <span>{max}</span>
        <span>{Math.round(max / 2)}</span>
        <span>0</span>
      </div>
      <div className="plot">
        <div className="grid-lines">
          <i />
          <i />
          <i />
        </div>
        <div className="bars">
          {data.map((x, i) => (
            <div
              className={"bar-column" + (x.observed ? "" : " unobserved")}
              key={i}
              title={x.observed ? `${clock(x.ts)} · ${x.requests} requests · ${x.errors} HTTP errors` : `${clock(x.ts)} · No telemetry samples`}
            >
              <div
                className="bar-total"
                style={{
                  height: `${Math.max(x.requests ? 2 : 0, ((x.requests ?? 0) / max) * 100)}%`,
                }}
              />
              <div
                className="bar-error"
                style={{ height: `${((x.errors ?? 0) / max) * 100}%` }}
              />
            </div>
          ))}
        </div>
        <div className="plot-times">
          <span>−2 min</span>
          <span>−1 min</span>
          <span>Now</span>
        </div>
      </div>
    </div>
  );
}
function IncidentRows({
  items,
  onSelect,
}: {
  items: Incident[];
  onSelect: (id: string) => void;
}) {
  return items.length ? (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Incident</th>
            <th>Category</th>
            <th>Severity</th>
            <th>Status</th>
            <th>Opened</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {items.map((i) => (
            <tr key={i.id} onClick={() => onSelect(i.id)}>
              <td>
                <button
                  className="text-button"
                  onClick={(e) => {
                    e.stopPropagation();
                    onSelect(i.id);
                  }}
                >
                  {i.title}
                </button>
                <small className="mono">
                  INC-{short(i.id)} · {i.subject}
                </small>
              </td>
              <td>{label(i.category)}</td>
              <td>
                <Badge value={i.severity} />
              </td>
              <td>
                <Badge value={i.status} />
              </td>
              <td className="mono">{clock(i.opened)}</td>
              <td>
                <ChevronRight size={16} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  ) : (
    <Empty
      title="No incidents"
      detail="Rules are monitoring. Run a scenario to see a detection."
    />
  );
}

function App() {
  const canOperate = privateConsole || sharedSession?.access === "master";
  const canConfigureAI = privateConsole;
  useEffect(() => {
    if (!sharedSession) return;
    const timer = setTimeout(() => window.location.assign("/login"), Math.max(0, Date.parse(sharedSession.expires_at) - Date.now()));
    return () => clearTimeout(timer);
  }, []);
  const [page, setPage] = useState<Page>("overview");
  const [overview, setOverview] = useState<Overview | null>(null);
  const [scenarios, setScenarios] = useState<Scenario[]>([]);
  const [runs, setRuns] = useState<Run[]>([]);
  const [limits, setLimits] = useState<SimulationLimits | null>(null);
  const [events, setEvents] = useState<TelemetryEvent[]>([]);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [agents, setAgents] = useState<AgentState | null>(null);
  const [agentIncident, setAgentIncident] = useState<string | null>(null);
  const [usageIncident, setUsageIncident] = useState("");
  const [focusedRun, setFocusedRun] = useState<string | null>(null);
  const [settings, setSettings] = useState<AI | null>(null);
  const [selected, setSelected] = useState("retry");
  const [duration, setDuration] = useState(50),
    [rps, setRps] = useState(6),
    [clients, setClients] = useState(3);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [toast, setToast] = useState("");
  const [connectionError, setConnectionError] = useState("");
  const [detail, setDetail] = useState<Incident | null>(null);
  const [search, setSearch] = useState("");
  const [rules, setRules] = useState<
    { id: string; name: string; condition: string; category: string }[]
  >([]);

  useEffect(() => { window.scrollTo(0, 0); }, [page]);
  useEffect(() => { if (page === "tester" && focusedRun) document.getElementById("run-" + focusedRun)?.scrollIntoView({block:"center"}); }, [page, focusedRun, runs.length]);

  async function refresh() {
    try {
      const [o, r, e, n, a, l] = await Promise.all([
        api<Overview>("/overview"),
        api<Run[]>("/runs"),
        api<{ events: TelemetryEvent[] }>("/events"),
        api<Notice[]>("/notices"),
        api<AgentState>("/agents"),
        api<SimulationLimits>("/limits"),
      ]);
      setOverview(o);
      setRuns(r);
      setEvents(e.events);
      setNotices(n);
      setAgents(a);
      setLimits(l);
      setConnectionError("");
    } catch (e) {
      setConnectionError((e as Error).message);
    }
  }
  useEffect(() => {
    let live = true;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      await refresh();
      if (live) timer = setTimeout(poll, 2000);
    };
    poll();
    api<Scenario[]>("/scenarios")
      .then(setScenarios)
      .catch((e) => setError(e.message));
    api<AI>("/settings/ai")
      .then((s) => setSettings(cleanSettings(s)))
      .catch((e) => setError(e.message));
    api<{ rules: typeof rules }>("/rules")
      .then((r) => setRules(r.rules))
      .catch((e) => setError(e.message));
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, []);
  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(""), 4500);
    return () => clearTimeout(t);
  }, [toast]);
  function cleanSettings(s: AI): AI {
    return {
      enabled: s.enabled,
      execution_mode: s.execution_mode,
      auto_analyze: s.auto_analyze,
      daily_job_limit: s.daily_job_limit,
      max_evidence_events: s.max_evidence_events,
      provider: s.provider,
      region: s.region,
      model_id: s.model_id,
      max_input_tokens: s.max_input_tokens,
      max_output_tokens: s.max_output_tokens,
      max_tool_calls: s.max_tool_calls,
      timeout_seconds: s.timeout_seconds,
      daily_budget_usd: s.daily_budget_usd,
      monthly_budget_usd: s.monthly_budget_usd,
      max_prompt_bytes: s.max_prompt_bytes,
      judge_enabled: s.judge_enabled,
      agent_profiles: s.agent_profiles,
    };
  }
  const activeRun = runs.find((r) =>
    ["running", "settling"].includes(r.status),
  );
  // Runs can also be started by the CLI or another browser. Keep the idle
  // draft intact, but show the server's actual configuration while running.
  const displayedScenario = activeRun?.scenario ?? selected;
  const displayedFlow = activeRun?.config ?? { duration, rps, clients, warmup: 5 };
  async function action(fn: () => Promise<void>) {
    setBusy(true);
    setError("");
    try {
      await fn();
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function selectIncident(id: string) {
    try {
      setDetail(await api<Incident>("/incidents/" + id));
      setPage("investigation");
    } catch (e) {
      setError((e as Error).message);
    }
  }
  const start = () =>
    action(async () => {
      await api("/runs", {
        method: "POST",
        body: JSON.stringify({
          scenario: selected,
          duration,
          rps,
          clients,
          warmup: 5,
        }),
      });
      setToast("Experiment started on the target service");
    });
  const stop = () =>
    action(async () => {
      await api("/runs/stop", { method: "POST" });
      setToast("Load stopped and recovery requested");
    });
  const save = () =>
    action(async () => {
      await api("/settings/ai", {
        method: "PUT",
        body: JSON.stringify(settings),
      });
      setToast(settings?.enabled ? "AI settings saved. The selected provider may process admitted incidents." : "Settings saved. AI execution disabled.");
    });
  function exportRun(run: Run) {
    const url = URL.createObjectURL(
      new Blob([JSON.stringify(run, null, 2)], { type: "application/json" }),
    );
    const a = document.createElement("a");
    a.href = url;
    a.download = `incidentlab-${run.id}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }
  const telemetryLive = overview?.data_state === "live" && !connectionError;
  const metric = telemetryLive ? overview?.resources : null;
  const filtered = (overview?.incidents || []).filter((i) =>
    (i.title + " " + i.category + " " + i.subject)
      .toLowerCase()
      .includes(search.toLowerCase()),
  );

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a
          className="brand"
          href="#"
          onClick={(e) => {
            e.preventDefault();
            setPage("overview");
          }}
        >
          <span className="brand-icon">
            <Activity size={23} />
          </span>
          <span>
            incident<span className="brand-light">lab</span>
            <small>SECURITY & RELIABILITY</small>
          </span>
        </a>
        <div className="workspace">
          <span className="workspace-icon">
            <FlaskConical size={17} />
          </span>
          <div>
            Laboratory<small>Experiment environment</small>
          </div>
          <span className="workspace-dot" />
        </div>
        <span className="nav-label">WORKSPACE</span>
        <nav>
          {pages.map(({ id, label: navLabel, icon: Icon }) => (
            <button
              key={id}
              onClick={() => {
                setPage(id);
                if (id === "agents") setAgentIncident(null);
                if (id === "usage") setUsageIncident("");
                setSearch("");
              }}
              className={page === id || page === "investigation" && id === "incidents" ? "nav-item active" : "nav-item"}
            >
              <Icon size={19} />
              <span>{navLabel}</span>
              {id === "incidents" && !!overview?.active_incidents && (
                <b>{overview.active_incidents}</b>
              )}
              {id === "agents" && <em>{agents?.enabled ? "ON" : "OFF"}</em>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="ai-note">
            <Bot size={18} />
            <div>
              {agents?.enabled ? agents.mode === "cloud_api" ? "Amazon Bedrock" : "Codex local mode" : "AI paused"}<small>{agents?.calls ?? "—"} attempts · {agents?.tokens ?? "—"} reported tokens</small>
            </div>
            <span className="quiet-dot" />
          </div>
          <div className="build-label">
            <span>POC / v0.2 · .NET</span>
            <span>TELEMETRY STATUS</span>
          </div>
          <small className="infra-note">
            {connectionError
              ? "Monitor unreachable"
              : !overview
                ? "Connecting to monitor"
                : `${overview.backend === "opensearch" ? "OpenSearch" : "Development SQLite"} · ${telemetryLive ? "live measurements" : "waiting for fresh telemetry"}`}
          </small>
        </div>
      </aside>
      <main>
        <header className="topbar">
          <div className="breadcrumb">
            Workspace <ChevronRight size={13} />{" "}
            <strong>{page === "investigation" ? "Incident investigation" : pages.find((p) => p.id === page)?.label}</strong>
          </div>
          <div className="top-status">
            {sharedSession && <form method="post" action="/auth/logout" className="shared-session">
              <span>{sharedSession.access === "master" ? "Personal master" : "Read-only guest"} · until {new Date(sharedSession.expires_at).toLocaleTimeString([], {hour: "2-digit", minute: "2-digit"})}</span>
              <input type="hidden" name="__RequestVerificationToken" value={sharedSession.csrf_token} />
              <button type="submit" className="secondary">Sign out</button>
            </form>}
            <span
              className={
                "live-dot " +
                (connectionError ? "degraded" : overview?.health || "waiting")
              }
            />
            {connectionError
              ? "Connection lost"
              : label(overview?.health || "waiting")}
            <span className="divider" />
            <span className="mono">
              {overview ? overview.backend === "sqlite" ? "DEV · SQLite" : "OpenSearch" : "Connecting"}
            </span>
            <button
              aria-label="Open notifications"
              className="icon-button"
              onClick={() => setPage("inbox")}
            >
              <Bell size={19} />
              {notices.length > 0 && <i />}
            </button>
          </div>
        </header>
        <div className="content">
          {sharedSession && <div className="shared-notice">{sharedSession.access === "master" ? `Personal master session. ${limits?.simulations.unlimited ? "No daily simulation cap. " : ""}AI quotas and budgets still apply; AI settings require the private console. Do not share this account.` : "Shared read-only session. You can explore real events and results; running experiments requires a personal operator account or the owner's private console."}</div>}
          {!privateConsole && !sharedSession && <div className="shared-notice">Access verification unavailable. Actions remain disabled. Reload to verify your session.</div>}
          {(error || connectionError) && (
            <div role="alert" className="error-banner">
              <TriangleAlert size={17} />
              {error || `Data is out of date: ${connectionError}`}
              <button aria-label="Dismiss error" onClick={() => setError("")}>
                <X size={16} />
              </button>
            </div>
          )}
          {page === "overview" && (
            <>
              <div className="page-heading">
                <div>
                  <div className="eyebrow">OBSERVE. GENERATE. VERIFY.</div>
                  <h1>Your laboratory, in real time.</h1>
                  <p>
                    From requests to signals. Every incident has a verifiable
                    story.
                  </p>
                </div>
                <button className="primary" onClick={() => setPage("tester")}>
                  <Play size={16} /> Run an experiment
                </button>
              </div>
              <div className="telemetry-status" role="status">
                <Radio size={17} />
                <span><strong>{telemetryLive ? "LIVE TELEMETRY" : "LIVE DATA UNAVAILABLE"}</strong>{" · "}
                  {telemetryLive
                    ? "HTTP events and process measurements collected from the target. No generated metrics."
                    : "Waiting for fresh target telemetry. Current measurements are hidden, not replaced with zeros."}
                  {telemetryLive && <small>{number(overview?.request_count)} observed requests / {overview?.request_window_seconds} s · {number(overview?.pending_events)} events awaiting indexing</small>}
                </span>
              </div>
              <div className="metrics-grid">
                <Metric
                  title="Requests / second"
                  value={number(telemetryLive ? overview?.requests_per_second : null)}
                  caption="Average over the last 20 seconds"
                  icon={Activity}
                />
                <Metric
                  title="Open incidents"
                  value={number(connectionError ? null : overview?.active_incidents)}
                  caption="Active and recovering signals"
                  icon={ShieldCheck}
                  tone={overview?.active_incidents ? "amber" : ""}
                />
                <Metric
                  title="Service errors"
                  value={telemetryLive && overview?.error_rate != null ? number(overview.error_rate) + "%" : "—"}
                  caption="5xx / observed requests; — if none"
                  icon={TriangleAlert}
                />
                <Metric
                  title="AI invocation attempts"
                  value={number(agents?.calls)}
                  caption={agents?.enabled ? agents.mode === "cloud_api" ? "Amazon Bedrock · metered usage" : "Codex local worker · account usage" : "AI execution disabled"}
                  icon={Bot}
                  tone="muted"
                />
              </div>
              <div className="overview-grid">
                <section className="panel traffic-panel">
                  <div className="panel-heading">
                    <div>
                      <h2>Service traffic</h2>
                      <p>Actual requests received by the API</p>
                    </div>
                    <div className="legend">
                      <span>
                        <i />
                        Requests
                      </span>
                      <span>
                        <i className="orange" />
                        4xx / 5xx
                      </span>
                      <b>2 MIN</b>
                    </div>
                  </div>
                  <Traffic data={telemetryLive ? overview?.histogram || [] : []} />
                  <p className="chart-note">Shaded intervals have no telemetry samples, not zero traffic.</p>
                  <div className="panel-footer">
                    <span>
                      <Radio size={14} /> target-api
                    </span>
                    <span>
                      {number(overview?.total_events)} events collected
                    </span>
                  </div>
                </section>
                <section className="panel resources">
                  <div className="panel-heading">
                    <div>
                      <h2>Process resources</h2>
                      <p>Real measurements, not host-wide metrics</p>
                    </div>
                    <Cpu size={20} />
                  </div>
                  <Resource
                    label="CPU"
                    value={metric?.cpu_percent}
                    unit="% of one core"
                    max={100}
                  />
                  <Resource
                    label="RSS memory"
                    value={metric?.memory_mb}
                    unit="MiB"
                    max={512}
                  />
                  <div className="freshness">
                    <span>Telemetry lag</span>
                    <strong
                      className={
                        overview?.health === "degraded" ? "text-amber" : ""
                      }
                    >
                      {number(overview?.source_lag)} s
                    </strong>
                  </div>
                  <div className="freshness">
                    <span>Last successful cycle</span>
                    <strong className="mono">
                      {clock(overview?.last_cycle)}
                    </strong>
                  </div>
                  {overview?.error && (
                    <p className="text-amber tiny">{overview.error}</p>
                  )}
                </section>
              </div>
              <section className="panel">
                <div className="panel-heading">
                  <div>
                    <h2>
                      Recent incidents{" "}
                      <span className="count">
                        {overview?.incidents.length || 0}
                      </span>
                    </h2>
                    <p>
                      Rule-based detections, evidence and incident status
                    </p>
                  </div>
                  <button
                    className="text-link"
                    onClick={() => setPage("incidents")}
                  >
                    All incidents <ArrowRight size={15} />
                  </button>
                </div>
                <IncidentRows
                  items={(overview?.incidents || []).slice(0, 4)}
                  onSelect={selectIncident}
                />
              </section>
              <div className="bottom-grid">
                <section className="panel">
                  <div className="panel-heading">
                    <h2>Analysis pipeline</h2>
                    <span className="tag">EVENT DRIVEN</span>
                  </div>
                  <div className="pipeline">
                    {[
                      ["API", "Real traffic"],
                      ["Telemetry", "Event collection"],
                      ["Analyzer", "Rules and patterns"],
                      ["Incidents", "Lab inbox"],
                    ].map(([a, b], i) => (
                      <React.Fragment key={a}>
                        <div>
                          <span className="step-dot">{i + 1}</span>
                          <strong>{a}</strong>
                          <small>{b}</small>
                        </div>
                        {i < 3 && <ChevronRight size={16} />}
                      </React.Fragment>
                    ))}
                  </div>
                  <div className="note-footer">
                    <Bot size={16} /> {agents?.enabled ? "Investigator → manager → policy → notifications. See Agents for live execution." : "The owner can enable an AI provider in Settings to investigate incidents."}
                  </div>
                </section>
                <section className="panel">
                  <div className="panel-heading">
                    <h2>Activity log</h2>
                  </div>
                  <div className="activity-list">
                    {overview?.activity.length ? (
                      overview.activity.slice(0, 4).map((a) => (
                        <div key={a.id}>
                          <span className="activity-point" />
                          <p>
                            {a.message}
                            <small>{clock(a.ts)}</small>
                          </p>
                        </div>
                      ))
                    ) : (
                      <p className="quiet">
                        Laboratory activity will appear here.
                      </p>
                    )}
                  </div>
                </section>
              </div>
            </>
          )}
          {page === "tester" && (
            <>
              <Heading
                eyebrow="CONTROLLED EXPERIMENTS"
                title="Create a signal. Observe the response."
                subtitle="Real HTTP requests to the target API. Faults affect the service; detectors see only measured telemetry."
              />
              <div className="tester-layout">
                <div>
                  <h3 className="section-label">01 / CHOOSE A SCENARIO</h3>
                  <div className="scenario-grid">
                    {scenarios.map((s) => (
                      <button
                        className={
                          "scenario-card " +
                          (displayedScenario === s.id ? "selected" : "")
                        }
                        key={s.id}
                        onClick={() => setSelected(s.id)}
                        disabled={!!activeRun}
                      >
                        <span className="scenario-top">
                          <span className="scenario-icon">
                            {s.id === "probing" ? (
                              <ShieldCheck size={20} />
                            ) : s.id === "retry" ? (
                              <Repeat2 size={20} />
                            ) : s.category === "Resources" ? (
                              <Cpu size={20} />
                            ) : s.id === "normal" ? (
                              <Activity size={20} />
                            ) : (
                              <Radio size={20} />
                            )}
                          </span>
                          <span className="tag">{s.category}</span>
                          {displayedScenario === s.id && <Check size={16} />}
                        </span>
                        <strong>{s.name}</strong>
                        <p>{s.description}</p>
                      </button>
                    ))}
                  </div>
                </div>
                <section className="panel configure">
                  <h3 className="section-label">02 / {activeRun ? "ACTIVE FLOW" : "CONFIGURE THE FLOW"}</h3>
                  <label>
                    Traffic duration <b>{displayedFlow.duration} s</b>
                    <input
                      type="range"
                      min="30"
                      max="180"
                      step="10"
                      value={displayedFlow.duration}
                      disabled={!!activeRun}
                      onChange={(e) => setDuration(+e.target.value)}
                    />
                  </label>
                  <label>
                    Requests per second <b>{displayedFlow.rps}</b>
                    <input
                      type="range"
                      min="1"
                      max="30"
                      value={displayedFlow.rps}
                      disabled={!!activeRun}
                      onChange={(e) => setRps(+e.target.value)}
                    />
                  </label>
                  <label>
                    Concurrent clients <b>{displayedFlow.clients}</b>
                    <input
                      type="range"
                      min="1"
                      max="10"
                      value={displayedFlow.clients}
                      disabled={!!activeRun}
                      onChange={(e) => setClients(+e.target.value)}
                    />
                  </label>
                  <div className="scenario-timeline">
                    <div>
                      <i />{displayedFlow.warmup} s of normal traffic
                    </div>
                    <div>
                      <i className="orange" />
                      {displayedFlow.duration - displayedFlow.warmup - 5} s of {displayedScenario === "normal" ? "normal traffic" : "the selected fault"}
                    </div>
                    <div>
                      <i />5 s of recovery + settling
                    </div>
                  </div>
                  <p className="tiny quiet">
                    Concurrency and response time may reduce the actual
                    request rate. Results are compared with the expected
                    rules.
                  </p>
                  {activeRun ? (
                    <button
                      className="danger wide"
                      onClick={stop}
                      disabled={busy || !canOperate}
                    >
                      <Square size={15} /> Stop and recover
                    </button>
                  ) : (
                    <button
                      className="primary wide"
                      onClick={start}
                      disabled={busy || !scenarios.length || !canOperate || !limits || limits.simulations.remaining === 0}
                    >
                      <Play size={16} />
                      {busy ? "Starting…" : "Start experiment"}
                    </button>
                  )}
                  <div className="safe-label">
                    <ShieldCheck size={14} /> Fixed target: laboratory
                    API
                  </div>
                  <p className="tiny quiet" role="status">{limits ? limits.simulations.unlimited ? "Personal master: no daily simulation limit. One experiment at a time; duration, traffic bounds and AI budgets still apply." : `${limits.simulations.remaining} of ${limits.simulations.daily_limit} non-master simulations remaining today. Resets at 00:00 UTC. Stopped or failed runs count.` : "Checking daily simulation allowance…"}</p>
                </section>
              </div>
              {activeRun && (
                <section className="run-live">
                  <div>
                    <span className="live-dot healthy" />
                    <strong>
                      {scenarios.find((s) => s.id === activeRun.scenario)?.name}
                    </strong>
                    <Badge value={activeRun.status} />
                    <span className="quiet">
                      {label(activeRun.result.phase)}
                    </span>
                  </div>
                  <div>
                    <b>{activeRun.result.sent}</b> HTTP attempts{" "}
                    <b>{number(activeRun.result.responses)}</b> responses{" "}
                    <b>{activeRun.result.http_errors}</b> HTTP errors{" "}
                    <b>{activeRun.result.transport_errors}</b> transport failures <span>AI activity is tracked separately in Agents</span>
                  </div>
                  <progress
                    max={activeRun.config.duration}
                    value={Math.min(
                      activeRun.config.duration,
                      Date.now() / 1000 - activeRun.started,
                    )}
                  />
                </section>
              )}
              <section className="panel">
                <div className="panel-heading">
                  <div>
                    <h2>Experiment runs</h2>
                    <p>Compare expected rules with actual detections</p>
                  </div>
                </div>
                {runs.length ? (
                  <div className="table-wrap">
                    <table>
                      <thead>
                        <tr>
                          <th>Scenario</th>
                          <th>Status</th>
                          <th>HTTP attempts</th>
                          <th>Evaluation</th>
                          <th>Started</th>
                          <th />
                        </tr>
                      </thead>
                      <tbody>
                        {runs.map((r) => (
                          <tr key={r.id} id={"run-" + r.id} className={focusedRun === r.id ? "focused-run" : undefined}>
                            <td>
                              {scenarios.find((s) => s.id === r.scenario)?.name}
                              <small className="mono">RUN-{short(r.id)}</small>
                              {r.result.error && (
                                <small className="text-amber">
                                  {r.result.error}
                                </small>
                              )}
                            </td>
                            <td>
                              <Badge value={r.status} />
                            </td>
                            <td>{r.result.sent}<small>{number(r.result.responses)} responses · {r.result.transport_errors} transport failures</small></td>
                            <td>
                              {r.result.evaluation === "matched" ? (
                                <span className="text-green">
                                  Expected and observed results match
                                </span>
                              ) : r.result.evaluation === "review" ? (
                                <span className="text-amber">
                                  Needs review ·{" "}
                                  {(r.result.missing || []).length} missing
                                </span>
                              ) : (
                                label(r.result.phase)
                              )}
                              {r.result.detected && (
                                <small>
                                  {r.result.detected.join(", ") ||
                                    "No rules triggered"}
                                </small>
                              )}
                              {!!r.result.unresolved?.length && (
                                <small className="text-amber">Unresolved: {r.result.unresolved.join(", ")}</small>
                              )}
                              {!!r.result.additional?.length && (
                                <small className="text-amber">Additional: {r.result.additional.join(", ")}</small>
                              )}
                              {r.result.recovery_confirmed && r.status === "completed" && (
                                <small className="text-green">Recovery confirmed</small>
                              )}
                              <div className="run-investigations">{r.incidents?.length ? r.incidents.map(i => <button className="text-button" key={i.id} onClick={() => selectIncident(i.id)}>Open investigation · INC-{short(i.id)}</button>) : <small>No recorded request-linked incident. Older runs and metric-only incidents may have no correlation.</small>}</div>
                            </td>
                            <td className="mono">{eventDate(r.started)}</td>
                            <td>
                              <button
                                className="icon-button"
                                aria-label="Export result as JSON"
                                onClick={() => exportRun(r)}
                              >
                                <ArrowDownToLine size={17} />
                              </button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <Empty
                    icon={FlaskConical}
                    title="Your first experiment starts here"
                    detail="Choose a scenario and watch events in the console."
                  />
                )}
              </section>
            </>
          )}
          {page === "incidents" && (
            <>
              <Heading
                eyebrow="SIGNALS AND CONTEXT"
                title="Incident log"
                subtitle="Rule detections and evidence. One AI investigation per incident when enabled."
              />
              <div className="searchbox">
                <Search size={18} />
                <input
                  aria-label="Search incidents"
                  placeholder="Search by issue, category or subject…"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
              </div>
              <section className="panel">
                <IncidentRows items={filtered} onSelect={selectIncident} />
              </section>
              <section className="panel">
                <div className="panel-heading">
                  <h2>Active rules</h2>
                  <span className="tag">NO LLM</span>
                </div>
                <div className="rule-list">
                  {rules.map((r) => (
                    <div key={r.id}>
                      <CircleDot size={16} />
                      <strong>{r.name}</strong>
                      <span>{r.condition}</span>
                      <small>{label(r.category)}</small>
                    </div>
                  ))}
                </div>
              </section>
            </>
          )}
          {page === "events" && (
            <>
              <Heading
                eyebrow="VERIFIABLE TELEMETRY"
                title="Service events"
                subtitle="The latest 150 events in the current window, collected from the API and process."
              />
              <section className="panel">
                {events.length ? (
                  <div className="table-wrap">
                    <table>
                      <thead>
                        <tr>
                          <th>Time</th>
                          <th>Type</th>
                          <th>Event</th>
                          <th>Outcome</th>
                          <th>Duration</th>
                          <th>ID</th>
                        </tr>
                      </thead>
                      <tbody>
                        {events.map((e) => (
                          <tr key={e.id}>
                            <td className="mono">{clock(e.ts)}</td>
                            <td>
                              <span className="tag">{e.kind}</span>
                            </td>
                            <td className="mono">
                              {e.kind === "metric"
                                ? `CPU ${number(e.cpu_percent)}% · RSS ${number(e.memory_mb)} MiB`
                                : e.path || e.message}
                            </td>
                            <td>
                              {e.status > 0 ? (
                                <span
                                  className={
                                    e.status >= 400
                                      ? "text-amber"
                                      : "text-green"
                                  }
                                >
                                  {e.status}
                                </span>
                              ) : (
                                "—"
                              )}
                            </td>
                            <td>
                              {e.kind === "request"
                                ? number(e.duration_ms) + " ms"
                                : "—"}
                            </td>
                            <td className="mono quiet">{short(e.id)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <Empty
                    icon={Layers3}
                    title="Waiting for telemetry"
                    detail="Data will appear when the target service sends its first events."
                  />
                )}
              </section>
            </>
          )}
          {page === "agents" && <AgentsView state={agents} stale={!!connectionError} initialIncident={agentIncident} onIncident={selectIncident}/>}
          {page === "investigation" && detail && <IncidentWorkspace key={detail.id} id={detail.id} request={api} agents={agents} canOperate={canOperate} onBack={() => setPage("incidents")} onRun={id => { setFocusedRun(id); setPage("tester"); }} onUsage={id => { setUsageIncident(id); setPage("usage"); }}/>}
          {page === "usage" && <AIUsageView onIncident={selectIncident} initialIncident={usageIncident}/>}
          {page === "inbox" && (
            <>
              <Heading
                eyebrow="RULE EVENTS & AI ESCALATION"
                title="Notification inbox"
                subtitle="Internal laboratory notifications. No messages are sent to Slack, Telegram or email."
              />
              <section className="panel">
                {notices.length ? (
                  <div className="notices">
                    {notices.map((n) => (
                      <button
                        key={n.id}
                        onClick={() => selectIncident(n.incident_id)}
                      >
                        <span className={"notice-icon " + n.kind}>
                          {n.kind === "resolved" ? (
                            <CheckCircle2 size={20} />
                          ) : (
                            <Bell size={20} />
                          )}
                        </span>
                        <div>
                          <strong>{n.body.title}</strong>
                          <p>{n.body.message}</p>
                          <small>
                            INC-{short(n.incident_id)} · {n.channel} · {n.kind} · delivered internally
                          </small>
                        </div>
                        <span className="mono quiet">{clock(n.ts)}</span>
                        <ChevronRight size={17} />
                      </button>
                    ))}
                  </div>
                ) : (
                  <Empty
                    icon={Bell}
                    title="No notifications"
                    detail="Incident openings and resolutions will appear here."
                  />
                )}
              </section>
            </>
          )}
          {page === "settings" && (
            <>
              <Heading
                eyebrow="CONFIGURATION"
                title="AI execution settings"
                subtitle="Choose Codex on your Mac or Amazon Bedrock on AWS. Detection continues independently."
              />
              <div className="settings-layout">
                <section className="panel settings-card">
                  {settings && <fieldset className="settings-access" disabled={!canConfigureAI}><AISettingsForm value={settings} onChange={setSettings} onSave={save} busy={busy}/></fieldset>}
                </section>
                <aside className="panel settings-aside">
                  <div className="eyebrow">AWS DEPLOYMENT</div>
                  <h2>Separate monitor and target</h2>
                  <p>
                    The project includes configuration for two EC2 instances:
                    one monitors, the other receives the load.
                  </p>
                  <div className="small-stage">
                    <Terminal size={19} />
                    <span>
                      Monitor<small>Console · Analyzer · OpenSearch</small>
                    </span>
                  </div>
                  <div className="small-stage">
                    <Cpu size={19} />
                    <span>
                      Target<small>.NET API · Process telemetry</small>
                    </span>
                  </div>
                  <p className="tiny quiet">
                    This screen does not create cloud resources. Region, network
                    and deployment costs are configured separately.
                  </p>
                </aside>
              </div>
            </>
          )}
          <footer className="page-footer">
            <span>
              IncidentLab <span className="mono">/ v0.2 · .NET</span>
            </span>
            <span>Continuous detection. AI only when needed.</span>
          </footer>
        </div>
      </main>
      {toast && (
        <div role="status" className="toast">
          <CheckCircle2 size={18} />
          {toast}
        </div>
      )}
    </div>
  );
}

function Heading({
  eyebrow,
  title,
  subtitle,
}: {
  eyebrow: string;
  title: string;
  subtitle: string;
}) {
  return (
    <div className="page-heading">
      <div>
        <div className="eyebrow">{eyebrow}</div>
        <h1>{title}</h1>
        <p>{subtitle}</p>
      </div>
    </div>
  );
}
function Metric({
  title,
  value,
  caption,
  icon: Icon,
  tone = "",
}: {
  title: string;
  value: string;
  caption: string;
  icon: typeof Activity;
  tone?: string;
}) {
  return (
    <section className={"metric-card " + tone}>
      <div>
        <span>{title}</span>
        <Icon size={18} />
      </div>
      <strong>{value}</strong>
      <small>{caption}</small>
    </section>
  );
}
function Resource({
  label: title,
  value,
  unit,
  max,
}: {
  label: string;
  value: number | null | undefined;
  unit: string;
  max: number;
}) {
  return (
    <div className="resource">
      <div>
        <span>{title}</span>
        <strong>
          {number(value)} <small>{unit}</small>
        </strong>
      </div>
      {value != null && <div className="resource-track">
        <i style={{ width: `${Math.min(100, ((value || 0) / max) * 100)}%` }} />
      </div>}
    </div>
  );
}

// The private monitor has no /auth/session route; public access is mediated by the separate gateway.
fetch("/auth/session").then(async response => {
  if (response.ok) sharedSession = await response.json();
  else if (response.status === 404) privateConsole = true;
  else if (response.status === 401) window.location.assign("/login");
}).catch(() => {}).finally(() => createRoot(document.getElementById("root")!).render(<App />));
