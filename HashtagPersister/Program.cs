// See https://aka.ms/new-console-template for more information

// TODO: When persisting a Post document to Cosmos DB, use post.PartitionKey
// ("{UserId}_{YYYYMM}") as the partition key for the Posts container.
//
// After a successful upsert, check whether this is the first time this
// partition bucket has been seen for the user.  If so, upsert a document in
// the "UserPartitions" reverse-index container (partition key: /userId) to
// record that the user now has data in this month bucket.  This enables
// efficient per-user historical queries without cross-partition fan-out.
//
// Example upsert for the reverse-index document:
//   var reverseIndexDoc = new
//   {
//       id        = post.UserId,
//       userId    = post.UserId,
//       partitions = /* read-modify-write: add YYYYMM if not present */,
//       partitionCount = /* updated count */
//   };
//   await reverseIndexContainer.UpsertItemAsync(
//       reverseIndexDoc, new PartitionKey(post.UserId));

Console.WriteLine("Hello, World!");
