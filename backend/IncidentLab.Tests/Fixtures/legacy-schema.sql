CREATE TABLE activity (
	id VARCHAR(40) NOT NULL,
	ts FLOAT NOT NULL,
	type VARCHAR(60) NOT NULL,
	message VARCHAR(500) NOT NULL,
	PRIMARY KEY (id)
)

;

CREATE TABLE agent_events (
	id VARCHAR(40) NOT NULL,
	job_id VARCHAR(40) NOT NULL,
	ts FLOAT NOT NULL,
	stage VARCHAR(30) NOT NULL,
	message VARCHAR(500) NOT NULL,
	PRIMARY KEY (id)
)

;

CREATE TABLE agent_jobs (
	id VARCHAR(40) NOT NULL,
	incident_id VARCHAR(40) NOT NULL,
	ts FLOAT NOT NULL,
	updated FLOAT NOT NULL,
	status VARCHAR(30) NOT NULL,
	stage VARCHAR(30) NOT NULL,
	worker_id VARCHAR(80),
	lease VARCHAR(80),
	deadline FLOAT,
	packet JSON NOT NULL,
	settings JSON NOT NULL,
	investigation JSON,
	decision JSON,
	policy JSON,
	usage JSON NOT NULL,
	error VARCHAR(500),
	PRIMARY KEY (id),
	UNIQUE (incident_id)
)

;

CREATE TABLE agent_sessions (
	id VARCHAR(40) NOT NULL,
	job_id VARCHAR(40) NOT NULL,
	incident_id VARCHAR(40) NOT NULL,
	role VARCHAR(30) NOT NULL,
	started FLOAT NOT NULL,
	ended FLOAT,
	status VARCHAR(30) NOT NULL,
	config JSON NOT NULL,
	provider_session_id VARCHAR(100),
	usage JSON NOT NULL,
	report JSON,
	error VARCHAR(100),
	PRIMARY KEY (id),
	UNIQUE (job_id, role)
)

;

CREATE TABLE incidents (
	id VARCHAR(40) NOT NULL,
	fingerprint VARCHAR(240) NOT NULL,
	status VARCHAR(20) NOT NULL,
	severity VARCHAR(20) NOT NULL,
	category VARCHAR(60) NOT NULL,
	rule VARCHAR(60) NOT NULL,
	subject VARCHAR(160) NOT NULL,
	title VARCHAR(240) NOT NULL,
	opened FLOAT NOT NULL,
	updated FLOAT NOT NULL,
	last_signal FLOAT NOT NULL,
	healthy_since FLOAT,
	closed FLOAT,
	acknowledged BOOLEAN NOT NULL,
	evidence JSON NOT NULL,
	report JSON NOT NULL,
	PRIMARY KEY (id)
)

;

CREATE TABLE notices (
	id VARCHAR(40) NOT NULL,
	incident_id VARCHAR(40) NOT NULL,
	ts FLOAT NOT NULL,
	kind VARCHAR(30) NOT NULL,
	channel VARCHAR(30) NOT NULL,
	body JSON NOT NULL,
	PRIMARY KEY (id)
)

;

CREATE TABLE runs (
	id VARCHAR(40) NOT NULL,
	scenario VARCHAR(40) NOT NULL,
	status VARCHAR(30) NOT NULL,
	started FLOAT NOT NULL,
	ended FLOAT,
	config JSON NOT NULL,
	result JSON NOT NULL,
	PRIMARY KEY (id)
)

;

CREATE TABLE settings (
	"key" VARCHAR(80) NOT NULL,
	value JSON NOT NULL,
	PRIMARY KEY ("key")
)

;

CREATE TABLE telemetry (
	id VARCHAR(100) NOT NULL,
	ts FLOAT NOT NULL,
	received FLOAT NOT NULL,
	sent BOOLEAN NOT NULL,
	payload JSON NOT NULL,
	PRIMARY KEY (id)
)

;
CREATE INDEX ix_agent_events_job_id ON agent_events (job_id);
CREATE INDEX ix_agent_sessions_job_id ON agent_sessions (job_id);
CREATE INDEX ix_agent_sessions_incident_id ON agent_sessions (incident_id);
CREATE INDEX ix_incidents_fingerprint ON incidents (fingerprint);
CREATE INDEX ix_notices_incident_id ON notices (incident_id);
CREATE INDEX ix_telemetry_sent ON telemetry (sent);
CREATE INDEX ix_telemetry_ts ON telemetry (ts);
