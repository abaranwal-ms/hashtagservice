# Kafka Setup Specification (Azure – Learning Environment)

## Overview

This document defines the chosen Kafka-compatible messaging solution for a **personal Azure subscription**, intended for **learning and experimentation** rather than production workloads.

The goal is to provision a **low‑cost, low‑ops Kafka setup** that supports basic Kafka concepts such as topics, partitions, producers, and consumers, while avoiding the complexity and cost of running a full Kafka cluster.

---

## Requirements

### Functional Requirements

- Kafka-compatible endpoint
- Support for **at least 3 partitions**
- Ability to use standard Kafka producers/consumers
- Simple provisioning and teardown

### Workload Characteristics

- Ingestion volume: **~1,000 records every 5 minutes**
- Event rate: ~3–4 events/sec
- Expected payload size: small (≈1 KB per event)
- Use case: learning, PoCs, experimentation

### Non‑Functional Requirements

- Minimal operational overhead
- Suitable for a **personal Azure Pay‑As‑You‑Go subscription**
- Cost‑efficient at very low throughput
- No need to manage brokers, disks, or Zookeeper

---

## Evaluated Options

### 1. Azure Event Hubs (Kafka Endpoint)

- Fully managed event streaming service
- Provides **Apache Kafka–compatible protocol**
- No broker or cluster management
- Scales via Throughput Units (TUs)

✅ **Best fit for learning and low-throughput workloads**

---

### 2. Azure HDInsight Kafka

- Full Apache Kafka cluster with brokers and Zookeeper
- Requires multiple VMs and storage disks
- High base cost even when idle
- Operationally heavy for learning use cases

❌ **Overkill for personal subscription**

---

### 3. Confluent Cloud on Azure

- Enterprise-grade managed Kafka
- Rich ecosystem (connectors, Flink, governance)
- Higher minimum cost and billing complexity

❌ **Not cost-optimal at very small scale**

---

## Final Decision

✅ **Azure Event Hubs (Standard tier) with Kafka-compatible endpoint**

This provides Kafka semantics (topics, partitions, offsets, consumer groups) with **dramatically lower cost and zero operational burden**, making it ideal for learning.

---

## Selected Architecture

### Azure Resources

- **Event Hubs Namespace**
  - Tier: **Standard**
  - Throughput Units: **1**
- **Event Hub**
  - Partitions: **3**
  - Retention: 1–7 days (default is sufficient)

### Kafka Mapping

| Kafka Concept   | Azure Event Hubs           |
| --------------- | -------------------------- |
| Kafka Cluster   | Event Hubs Namespace       |
| Kafka Topic     | Event Hub                  |
| Kafka Partition | Event Hub Partition        |
| Kafka Broker    | Fully managed (abstracted) |

---

## Capacity Justification

### Required Throughput

- ~3.3 events/sec
- ~3.3 KB/sec ingestion

### Provided Capacity (1 TU)

- **Ingress**: 1 MB/sec
- **Events/sec**: up to 1,000

➡️ Actual usage is **well under 1%** of allocated capacity.

---

## Cost Estimate (Approximate)

- 1 Throughput Unit: ~$0.03/hour
- Monthly cost (≈730 hours): **~$20–25 USD**
- Event ingress cost: negligible at this scale

This keeps the setup affordable for continuous experimentation.

---

## Connectivity Details (Kafka Clients)

### Bootstrap Server

```text
<namespace>.servicebus.windows.net:9093
```

### Authentication

- SASL/PLAIN
- Username: `$ConnectionString`
- Password: Event Hubs Shared Access Policy connection string

### Kafka Compatibility

- Works with common Kafka libraries (Java, Python, .NET, Go)
- No application code changes required beyond config

---

## Scaling and Future Considerations

- Increase partitions if parallelism needs grow
- Add Throughput Units if ingestion increases
- For production or advanced Kafka features:
  - Migrate to Confluent Cloud or self-managed Kafka
- Event Hubs allows gradual progression without re‑architecting producers/consumers

---

## Conclusion

Azure Event Hubs (Kafka endpoint) provides the **best balance of cost, simplicity, and Kafka compatibility** for a personal Azure subscription and learning-focused workload. It enables hands-on Kafka experience without the operational or financial overhead of running a full Kafka cluster.
