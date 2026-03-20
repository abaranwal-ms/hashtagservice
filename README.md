# HashtagService

An event-driven microservices architecture built with .NET 8, Azure Event Hubs, and Azure Cosmos DB designed to extract, aggregate, and query trending hashtags from a high-throughput stream of social media posts.

![Hashtag Posts Table UI](./docs/images/HashTag-Posts.png)

## Overview

The HashtagService pipeline ingests raw social posts, extracts hashtags, aggregates counts in memory to reduce downstream volume, and persists the aggregated data into Cosmos DB for querying.

For a deep dive into the system flows, see the **[Architecture Diagram](docs/architecture.md)**.

## Architecture Highlights

- **Post Generator**: Multi-threaded producer simulating a high-throughput stream of `Post` events into the `posts-topic`.
- **Hashtag Extractor**: Event Hub consumer that extracts hashtags, performs in-memory rolling batch aggregation (swapping every ~100 posts to optimize write load), and publishes `{hashtag, count, timestamp}` to the `hashtags-topic`.
- **Hashtag Persister**: Event Hub consumer that reads trending tag counts and performs additive merge upserts into Cosmos DB `Hashtags` container (at-least-once safe).
- **Post Persister**: Event Hub consumer that reads raw posts from `posts-topic` and stores them in the Cosmos DB `Posts` container.
- **User View API**: Read-only query API for fetching trending hashtags and post metadata from Cosmos DB.

## Infrastructure

This project leverages:
- **Azure Event Hubs** for Kafka-compatible decoupled messaging streams.
- **Azure Blob Storage** for Event Hub consumer processor checkpoints.
- **Azure Cosmos DB** (NoSQL API) for fast, partitioned reads and writes.

Check the `infra/` folder for Bicep templates used to provision the Azure infrastructure.

## Solutions Map

| Project | Description |
|---|---|
| `PostGenerator/` | Simulates load by publishing posts to Event Hubs |
| `HashtagExtractor/` | Consumes posts, extracts & counts hashtags |
| `HashtagPersister/` | Consumes hashtags, persists trending stats |
| `UserView/` | Query API for UI consumption *(Target State)* |
| `Shared/` | Contains data models & Cosmos DB Handlers |
| `Shared.Tests/` | Live Cosmos DB Integration tests |

## How to use AI Agents

This repository is optimized for use with GitHub Copilot. Agent instruction files are provided to quickly onboard Copilot to the context of each service or infrastructure layer:

- `@workspace #PostCreator` (PostGenerator project context)
- `@workspace #HashTagCounter` (HashtagExtractor project context)
- `@workspace #HashTagPersister` (HashtagPersister project context)
- `@workspace #Database` (Cosmos DB modeling and queries)
- `@workspace #Kafka` (Event Hubs messaging topology)
