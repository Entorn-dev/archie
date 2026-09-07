package main

import "testing"

func TestCatalogueExposesDeterministicAvailability(t *testing.T) {
	if len(books) != 2 || books[0].ID != "book-1984" || books[0].Available != 7 {
		t.Fatalf("unexpected catalogue: %#v", books)
	}
}
