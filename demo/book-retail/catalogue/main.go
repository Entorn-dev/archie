package main

import (
	"encoding/json"
	"log"
	"net/http"
)

type book struct {
	ID        string `json:"id"`
	Title     string `json:"title"`
	Available int    `json:"available"`
}

var books = []book{
	{ID: "book-1984", Title: "Nineteen Eighty-Four", Available: 7},
	{ID: "book-earthsea", Title: "A Wizard of Earthsea", Available: 4},
}

func main() {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", jsonHandler(map[string]string{"status": "ready"}))
	mux.HandleFunc("GET /api/books", jsonHandler(books))
	log.Fatal(http.ListenAndServe("127.0.0.1:4401", mux))
}

func jsonHandler(value any) http.HandlerFunc {
	return func(response http.ResponseWriter, _ *http.Request) {
		response.Header().Set("Content-Type", "application/json")
		if err := json.NewEncoder(response).Encode(value); err != nil {
			http.Error(response, err.Error(), http.StatusInternalServerError)
		}
	}
}
