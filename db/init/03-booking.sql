\connect booking
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

CREATE TABLE booking (
    booking_id     uuid PRIMARY KEY,               -- UUID v7, index friendly
    user_id        text    NOT NULL,
    show_id        uuid    NOT NULL,
    hold_id        uuid    NOT NULL,
    status         text    NOT NULL CHECK (status IN ('PENDING_PAYMENT','CONFIRMED','FAILED','EXPIRED','CANCELLED')),
    total_minor    bigint  NOT NULL,
    currency       char(3) NOT NULL,
    payment_id     uuid    NULL,
    failure_reason text    NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now(),
    version        int     NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX ux_booking_hold ON booking (hold_id);       -- one booking per hold
CREATE INDEX ix_booking_user ON booking (user_id, created_at DESC);
CREATE INDEX ix_booking_pending ON booking (created_at) WHERE status = 'PENDING_PAYMENT';

-- Seats the booking intends to buy (snapshot of the hold + prices).
CREATE TABLE booking_line (
    booking_id  uuid   NOT NULL REFERENCES booking,
    seat_id     text   NOT NULL,
    category    text   NOT NULL,
    price_minor bigint NOT NULL,
    PRIMARY KEY (booking_id, seat_id)
);

-- Seats actually SOLD. The primary key is the double-sale guard (design §5, guard 2).
CREATE TABLE booking_seat (
    show_id     uuid   NOT NULL,
    seat_id     text   NOT NULL,
    booking_id  uuid   NOT NULL REFERENCES booking,
    price_minor bigint NOT NULL,
    sold_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (show_id, seat_id)
);
CREATE INDEX ix_booking_seat_booking ON booking_seat (booking_id);

-- Cancelled sales move here, freeing the seat for resale.
CREATE TABLE booking_seat_history (
    show_id     uuid   NOT NULL,
    seat_id     text   NOT NULL,
    booking_id  uuid   NOT NULL,
    price_minor bigint NOT NULL,
    sold_at     timestamptz NOT NULL,
    released_at timestamptz NOT NULL DEFAULT now()
);
