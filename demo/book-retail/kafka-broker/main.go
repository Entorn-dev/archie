package main

import (
	"context"
	"log"
	"os/signal"
	"syscall"

	"github.com/twmb/franz-go/pkg/kfake"
)

func main() {
	cluster, err := kfake.NewCluster(
		kfake.Ports(4403),
		kfake.AllowAutoTopicCreation(),
		kfake.DefaultNumPartitions(1),
		kfake.SeedTopics(1,
			"order.submitted",
			"inventory.reservation-requested",
			"inventory.reserved",
			"fulfilment.requested",
			"dispatch.requested"),
	)
	if err != nil {
		log.Fatal(err)
	}
	context, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	<-context.Done()
	cluster.Close()
}
