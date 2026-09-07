CREATE TABLE IF NOT EXISTS orders (
    id text PRIMARY KEY,
    book_id text NOT NULL,
    quantity integer NOT NULL,
    status text NOT NULL
);

CREATE TABLE IF NOT EXISTS inventory_reservations (
    order_id text PRIMARY KEY,
    book_id text NOT NULL,
    quantity integer NOT NULL,
    status text NOT NULL
);
