define(["require", "exports", "common/pagedList", "commands/getCollectionInfoCommand", "commands/getDocumentsCommand", "commands/getSystemDocumentsCommand", "commands/getAllDocumentsCommand", "common/pagedResultSet"], function(require, exports, pagedList, getCollectionInfoCommand, getDocumentsCommand, getSystemDocumentsCommand, getAllDocumentsCommand, pagedResultSet) {
    var collection = (function () {
        function collection(name, ownerDatabase) {
            this.name = name;
            this.ownerDatabase = ownerDatabase;
            this.colorClass = "";
            this.documentCount = ko.observable(0);
            this.isAllDocuments = false;
            this.isSystemDocuments = false;
            this.isAllDocuments = name === collection.allDocsCollectionName;
            this.isSystemDocuments = name === collection.systemDocsCollectionName;
        }
        // Notifies consumers that this collection should be the selected one.
        // Called from the UI when a user clicks a collection the documents page.
        collection.prototype.activate = function () {
            ko.postbox.publish("ActivateCollection", this);
        };

        collection.prototype.fetchTotalDocumentCount = function () {
            var _this = this;
            // AFAICT, there's no way to fetch just the total number of system
            // documents, other than doing a full fetch for sys docs.
            if (this.isSystemDocuments) {
                new getSystemDocumentsCommand(this.ownerDatabase, 0, 1024).execute().done(function (results) {
                    return _this.documentCount(results.totalResultCount);
                });
            } else {
                new getCollectionInfoCommand(this).execute().done(function (info) {
                    return _this.documentCount(info.totalResults);
                });
            }
        };

        collection.prototype.getDocuments = function () {
            if (!this.documentsList) {
                this.documentsList = this.createPagedList();
            }

            return this.documentsList;
        };

        collection.prototype.fetchDocuments = function (skip, take) {
            var _this = this;
            if (this.isSystemDocuments) {
                // System documents don't follow the normal paging rules. See getSystemDocumentsCommand.execute() for more info.
                var task = new getSystemDocumentsCommand(this.ownerDatabase, skip, take).execute();
                task.done(function (results) {
                    return _this.documentCount(results.totalResultCount);
                });
                return task;
            }
            if (this.isAllDocuments) {
                return new getAllDocumentsCommand(this.ownerDatabase, skip, take).execute();
            } else {
                return new getDocumentsCommand(this, skip, take).execute();
            }
        };

        collection.createSystemDocsCollection = function (ownerDatabase) {
            return new collection(collection.systemDocsCollectionName, ownerDatabase);
        };

        collection.createAllDocsCollection = function (ownerDatabase) {
            return new collection(collection.allDocsCollectionName, ownerDatabase);
        };

        collection.prototype.createPagedList = function () {
            var _this = this;
            var fetcher = function (skip, take) {
                return _this.fetchDocuments(skip, take);
            };
            var list = new pagedList(fetcher);
            list.collectionName = this.name;
            return list;
        };
        collection.allDocsCollectionName = "All Documents";
        collection.systemDocsCollectionName = "System Documents";
        return collection;
    })();

    
    return collection;
});
//# sourceMappingURL=collection.js.map
