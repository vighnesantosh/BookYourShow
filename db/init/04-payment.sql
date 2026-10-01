\connect payment
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

CREATE TABLE payment (
    payment_id    uuid PRIMARY KEY,
    booking_id    uuid    NOT NULL UNIQUE,            -- one intent per booking = gateway idempotency
    user_id       text    NOT NULL,
    gateway       text    NOT NULL,
    gateway_ref   text    NOT NULL,
    client_secret text    NOT NULL,
    status        text    NOT NULL CHECK (status IN ('REQUIRES_ACTION','SUCCEEDED','FAILED','CANCELLED','REFUNDED')),
    amount_minor  bigint  NOT NULL,
    currency      char(3) NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

-- Every webhook ever received; the primary key drops duplicates.
CREATE TABLE payment_event (
    gateway_event_id text PRIMARY KEY,
    payment_id       uuid  NOT NULL REFERENCES payment,
    type             text  NOT NULL,
    payload          jsonb NOT NULL,
    received_at      timestamptz NOT NULL DEFAULT now()
);
