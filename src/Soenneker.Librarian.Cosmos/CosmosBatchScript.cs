namespace Soenneker.Librarian.Cosmos;

public static class CosmosBatchScript
{
    public const string Id = "librarian_batch_v1";
    // Cosmos executes the whole procedure in a partition-scoped transaction. Throwing rolls back every write.
    public const string Body = """
        function execute(requestJson) {
            var request = JSON.parse(requestJson), context = getContext(), collection = context.getCollection();
            var conditions = request.conditions || [], writes = request.writes || [], checkedDocuments = [];
            function accepted(ok) { if (!ok) throw new Error("Librarian operation exceeds the Cosmos transaction execution budget."); }
            function done() { context.getResponse().setBody(true); }
            function read(id, callback) {
                accepted(collection.readDocument(collection.getAltLink() + "/docs/" + id, {}, function (error, document) {
                    if (error && error.number !== 404) throw error;
                    callback(error ? null : document);
                }));
            }
            function check(index) {
                if (index === conditions.length) return guard(0);
                var condition = conditions[index];
                read(condition.id, function (document) {
                    if ((document ? document.rawJson : null) !== condition.expected) return context.getResponse().setBody(false);
                    checkedDocuments.push(document);
                    check(index + 1);
                });
            }
            // Snapshot isolation alone permits write skew on read-only conditions. Include each condition in the
            // transaction's write set, without changing its value; absent guards are created and deleted atomically.
            function guard(index) {
                if (index === conditions.length) return write(0);
                var current = checkedDocuments[index];
                function next(error) { if (error) throw error; guard(index + 1); }
                if (current) accepted(collection.replaceDocument(current._self, current, {}, next));
                else accepted(collection.createDocument(collection.getSelfLink(), {
                    id:conditions[index].id, partitionKey:request.partitionKey
                }, {disableAutomaticIdGeneration:true}, function (error, placeholder) {
                    if (error) throw error;
                    accepted(collection.deleteDocument(placeholder._self, {}, next));
                }));
            }
            function write(index) {
                if (index === writes.length) return done();
                var operation = writes[index];
                read(operation.id, function (current) {
                    function next(error) { if (error) throw error; write(index + 1); }
                    if (operation.document === null) {
                        if (!current) return write(index + 1);
                        accepted(collection.deleteDocument(current._self, {}, next));
                    } else {
                        if (current) {
                            operation.document.originalId = current.originalId;
                            accepted(collection.replaceDocument(current._self, operation.document, {}, next));
                        } else accepted(collection.createDocument(collection.getSelfLink(), operation.document, {disableAutomaticIdGeneration:true}, next));
                    }
                });
            }
            function collect(continuation) {
                accepted(collection.queryDocuments(collection.getSelfLink(), {
                    query: "SELECT c.id FROM c WHERE c.containerName = @name",
                    parameters: [{name:"@name", value:request.clear}]
                }, {continuation:continuation}, function (error, documents, options) {
                    if (error) throw error;
                    for (var i = 0; i < documents.length; i++) writes.push({id:documents[i].id, document:null});
                    if (options.continuation) collect(options.continuation); else write(0);
                }));
            }
            if (request.clear !== undefined) collect(); else check(0);
        }
        """;
}
