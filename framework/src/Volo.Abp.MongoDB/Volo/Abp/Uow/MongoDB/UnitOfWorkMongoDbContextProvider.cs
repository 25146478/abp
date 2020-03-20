using System;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Volo.Abp.Data;
using Volo.Abp.MongoDB;

namespace Volo.Abp.Uow.MongoDB
{
    public class UnitOfWorkMongoDbContextProvider<TMongoDbContext> : IMongoDbContextProvider<TMongoDbContext>
        where TMongoDbContext : IAbpMongoDbContext
    {
        private readonly IUnitOfWorkManager _unitOfWorkManager;
        private readonly IConnectionStringResolver _connectionStringResolver;

        public UnitOfWorkMongoDbContextProvider(
            IUnitOfWorkManager unitOfWorkManager,
            IConnectionStringResolver connectionStringResolver)
        {
            _unitOfWorkManager = unitOfWorkManager;
            _connectionStringResolver = connectionStringResolver;
        }

        public TMongoDbContext GetDbContext()
        {
            var unitOfWork = _unitOfWorkManager.Current;
            if (unitOfWork == null)
            {
                throw new AbpException($"A {nameof(IMongoDatabase)} instance can only be created inside a unit of work!");
            }

            var connectionString = _connectionStringResolver.Resolve<TMongoDbContext>();
            var dbContextKey = $"{typeof(TMongoDbContext).FullName}_{connectionString}";

            var mongoUrl = new MongoUrl(connectionString);
            var databaseName = mongoUrl.DatabaseName;
            if (databaseName.IsNullOrWhiteSpace())
            {
                databaseName = ConnectionStringNameAttribute.GetConnStringName<TMongoDbContext>();
            }

            //TODO: Create only single MongoDbClient per connection string in an application (extract MongoClientCache for example).
            var databaseApi = unitOfWork.GetOrAddDatabaseApi(
                dbContextKey,
                () =>
                {
                    var client = new MongoClient(mongoUrl);
                    var database = client.GetDatabase(databaseName);

                    var dbContext = unitOfWork.ServiceProvider.GetRequiredService<TMongoDbContext>();

                    if (unitOfWork.Options.IsTransactional)
                    {
                        var session = client.StartSession();
                        session.StartTransaction();
                        
                        if (unitOfWork.Options.Timeout.HasValue)
                        {
                            //TODO: Test this.
                            session.AdvanceOperationTime(new BsonTimestamp((long)unitOfWork.Options.Timeout.Value.TotalSeconds));
                        }

                        var transactionApiKey = $"MongoDb_{mongoUrl}";
                        unitOfWork.AddTransactionApi(
                            transactionApiKey,
                            new MongoDbTransactionApi(session)
                        );
                        
                        dbContext.ToAbpMongoDbContext().InitializeDatabase(database, session);
                    }
                    else
                    {
                        dbContext.ToAbpMongoDbContext().InitializeDatabase(database, null);
                    }
                    
                    return new MongoDbDatabaseApi<TMongoDbContext>(dbContext);
                });

            return ((MongoDbDatabaseApi<TMongoDbContext>)databaseApi).DbContext;
        }
    }
}