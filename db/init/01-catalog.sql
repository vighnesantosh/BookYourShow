\connect catalog
-- Shared plumbing tables (outbox, inbox, idempotency) -------------------------
CREATE TABLE outbox (
    id           uuid PRIMARY KEY,
    event_type   text        NOT NULL,
    payload      jsonb       NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL
);
CREATE INDEX ix_outbox_unpublished ON outbox (created_at) WHERE published_at IS NULL;

CREATE TABLE processed_message (
    message_id   text        NOT NULL,
    consumer     text        NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (message_id, consumer)
);

CREATE TABLE idempotency_key (
    user_id       text        NOT NULL,
    key           text        NOT NULL,
    request_hash  text        NOT NULL,
    status_code   int         NULL,
    response_body text        NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, key)
);

CREATE TABLE venue (
    venue_id   uuid PRIMARY KEY,
    name       text NOT NULL,
    city       text NOT NULL,
    timezone   text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- layout = {"rows":[{"row":"A","seats":[{"number":1,"category":"GOLD","x":0,"y":0}, ...]}, ...]}
CREATE TABLE hall (
    hall_id        uuid PRIMARY KEY,
    venue_id       uuid NOT NULL REFERENCES venue,
    name           text NOT NULL,
    layout         jsonb NOT NULL,
    layout_version int  NOT NULL DEFAULT 1
);

CREATE TABLE event (
    event_id     uuid PRIMARY KEY,
    title        text NOT NULL,
    category     text NOT NULL,
    language     text NULL,
    duration_min int  NOT NULL,
    description  text NULL
);

CREATE TABLE show (
    show_id       uuid PRIMARY KEY,
    event_id      uuid NOT NULL REFERENCES event,
    hall_id       uuid NOT NULL REFERENCES hall,
    starts_at     timestamptz NOT NULL,
    sale_opens_at timestamptz NOT NULL,
    currency      char(3) NOT NULL,
    status        text NOT NULL CHECK (status IN ('DRAFT','PUBLISHED','CANCELLED'))
);
CREATE INDEX ix_show_event ON show (event_id, starts_at);

CREATE TABLE price_tier (
    show_id     uuid   NOT NULL REFERENCES show,
    category    text   NOT NULL,
    price_minor bigint NOT NULL CHECK (price_minor >= 0),
    PRIMARY KEY (show_id, category)
);
