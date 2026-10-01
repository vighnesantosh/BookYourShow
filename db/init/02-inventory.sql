\connect inventory
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

-- Durable seat state. Valkey holds the live holds; this table is what Valkey is rebuilt from.
CREATE TABLE show_seat (
    show_id    uuid NOT NULL,
    seat_id    text NOT NULL,
    category   text NOT NULL,
    status     text NOT NULL CHECK (status IN ('AVAILABLE','BOOKED')),
    booking_id uuid NULL,
    version    int  NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (show_id, seat_id)
);
