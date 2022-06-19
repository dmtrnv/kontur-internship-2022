using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microservices.Common;
using Microservices.Common.Exceptions;
using Microservices.ExternalServices.Authorization;
using Microservices.ExternalServices.Authorization.Types;
using Microservices.ExternalServices.Billing;
using Microservices.ExternalServices.Billing.Types;
using Microservices.ExternalServices.CatDb;
using Microservices.ExternalServices.CatDb.Types;
using Microservices.ExternalServices.CatExchange;
using Microservices.ExternalServices.CatExchange.Types;
using Microservices.ExternalServices.Database;
using Microservices.ExternalServices.Database.Types;
using Microservices.Types;
using Polly;

namespace Microservices
{
    public class CatShelterService : ICatShelterService
    {
        private readonly IDatabaseCollection<UserFavouriteCats, Guid> _favouriteCats;
        private readonly IDatabaseCollection<AdditionalCatInfo, Guid> _additionalCatInfo;
        private readonly IAuthorizationService _authorizationService;
        private readonly IBillingService _billingService;
        private readonly ICatInfoService _catInfoService;
        private readonly ICatExchangeService _catExchangeService;

        public CatShelterService(
            IDatabase database,
            IAuthorizationService authorizationService,
            IBillingService billingService,
            ICatInfoService catInfoService,
            ICatExchangeService catExchangeService)
        {
            _favouriteCats = database.GetCollection<UserFavouriteCats, Guid>("Favourite cats");
            _additionalCatInfo = database.GetCollection<AdditionalCatInfo, Guid>("Cats additional info");
            _authorizationService = authorizationService;
            _billingService = billingService;
            _catInfoService = catInfoService;
            _catExchangeService = catExchangeService;
        }

        public async Task<List<Cat>> GetCatsAsync(string sessionId, int skip, int limit, CancellationToken cancellationToken)
        {
            await AuthorizeAsync(sessionId, cancellationToken);
            
            return await GetCatsAsync(skip, limit, cancellationToken);
        }

        public async Task AddCatToFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            var authorizationResult = await AuthorizeAsync(sessionId, cancellationToken);
            
            if (await _additionalCatInfo.FindAsync(catId, cancellationToken) == null)
            {
                return;
            }

            var userFavouriteCats = await _favouriteCats.FindAsync(authorizationResult.UserId, cancellationToken);
            if (userFavouriteCats != null)
            {
                if (userFavouriteCats.CatsIds.Contains(catId))
                {
                    return;
                }
                
                userFavouriteCats.CatsIds.Add(catId);
                
                await _favouriteCats.WriteAsync(userFavouriteCats, cancellationToken);
            }
            else
            {
                await _favouriteCats.WriteAsync(new UserFavouriteCats
                    {
                        Id = authorizationResult.UserId,
                        CatsIds = new List<Guid> { catId }
                    }, 
                    cancellationToken);
            }
        }

        public async Task<List<Cat>> GetFavouriteCatsAsync(string sessionId, CancellationToken cancellationToken)
        {
            var authorizationResult = await AuthorizeAsync(sessionId, cancellationToken);

            var userFavouriteCats = await _favouriteCats.FindAsync(authorizationResult.UserId, cancellationToken);
            if (userFavouriteCats == null)
            {
                return new List<Cat>();
            }
            
            var cats = new List<Cat>();
            
            foreach (var id in userFavouriteCats.CatsIds)
            {
                try
                {
                    cats.Add(await GetCatByIdAsync(id, cancellationToken));
                }
                catch (InvalidRequestException)
                {
                    // Пропустить.
                }
            }
            
            return cats;
        }

        public async Task DeleteCatFromFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            var authorizationResult = await AuthorizeAsync(sessionId, cancellationToken);

            var userFavouriteCats = await _favouriteCats.FindAsync(authorizationResult.UserId, cancellationToken);
            if (userFavouriteCats == null)
            {
                return;
            }
            
            userFavouriteCats.CatsIds.Remove(catId);
            
            await _favouriteCats.WriteAsync(userFavouriteCats, cancellationToken);
        }

        public async Task<Bill> BuyCatAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            await AuthorizeAsync(sessionId, cancellationToken);
            
            var product = await GetProductFromBillingServiceAsync(catId, cancellationToken);
            var catsPriceHistory = await GetCatsPriceHistoryFromCatExchangeServiceAsync(new [] { product.BreedId }, cancellationToken);
            var catPrice = GetCatPriceHistoryAndCurrentPrice(product.BreedId, catsPriceHistory).Price;
            
            var bill = await SellProductOnBillingServiceAsync(catId, catPrice, cancellationToken);
            
            await DeleteCatFromFavouritesForEachUserAsync(catId, cancellationToken);
            await DeleteCatAdditionalInfoAsync(catId, cancellationToken);

            return bill;
        }

        public async Task<Guid> AddCatAsync(string sessionId, AddCatRequest request, CancellationToken cancellationToken)
        {
            var authorizationResult = await AuthorizeAsync(sessionId, cancellationToken);

            var catInfo = await GetCatInfoFromCatInfoServiceAsync(request.Breed, cancellationToken);
            var catId = Guid.NewGuid();

            await AddProductToBillingServiceAsync(new Product
                {
                    Id = catId,
                    BreedId = catInfo.BreedId
                },
                cancellationToken);
            
            await _additionalCatInfo.WriteAsync(new AdditionalCatInfo
                {
                    Id = catId,
                    AddedBy = authorizationResult.UserId,
                    Name = request.Name,
                    Photo = request.Photo
                },
                cancellationToken);
            
            return catId;
        }
        
        private async Task<AuthorizationResult> AuthorizeAsync(string sessionId, CancellationToken cancellationToken)
        {
            var authorizationResult = await PollyHelper.ExecuteWithRetryAndFallBack(
                async () => await _authorizationService.AuthorizeAsync(sessionId, cancellationToken));

            if (!authorizationResult.IsSuccess)
            {
                throw new AuthorizationException();
            }
            
            return authorizationResult;
        }
        
        private async Task<List<Cat>> GetCatsAsync(int skip, int limit, CancellationToken cancellationToken)
        {
            var products = await GetProductsFromBillingServiceAsync(skip, limit, cancellationToken);
            var breedIds = products
                .Select(p => p.BreedId)
                .Distinct()
                .ToArray();
            
            var getCatsInfoTask = GetCatsInfoFromCatInfoServiceAsync(breedIds, cancellationToken);
            var getCatsPriceHistoryTask = GetCatsPriceHistoryFromCatExchangeServiceAsync(breedIds, cancellationToken);
            Task.WhenAll(
                GetCatsInfoFromCatInfoServiceAsync(breedIds, cancellationToken),
                GetCatsPriceHistoryFromCatExchangeServiceAsync(breedIds, cancellationToken));

            var catsInfo = getCatsInfoTask.Result;
            var catsPriceHistory = getCatsPriceHistoryTask.Result;
            var cats = new List<Cat>(products.Count);
            
            foreach (var product in products)
            {
                var additionalCatInfo = await _additionalCatInfo.FindAsync(product.Id, cancellationToken);
                if (additionalCatInfo == null)
                {
                    continue;
                }

                var catInfo = catsInfo.FirstOrDefault(ci => ci.BreedId.Equals(product.BreedId));
                
                var cat = BuildCat(product, catInfo, additionalCatInfo, catsPriceHistory[product.BreedId]);

                cats.Add(cat);
            }
            
            return cats;
        }
        
        private async Task<Cat> GetCatByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            var product = await PollyHelper.ExecuteWithRetryAndFallBack(async () => await _billingService.GetProductAsync(id, cancellationToken));
            
            if (product == null)
            {
                throw new InvalidRequestException();
            }
            
            var getCatInfoTask = await PollyHelper.ExecuteWithRetryAndFallBack(async () => _catInfoService.FindByBreedIdAsync(product.BreedId, cancellationToken));
            var getCatPriceHistoryTask = await PollyHelper.ExecuteWithRetryAndFallBack(async () => _catExchangeService.GetPriceInfoAsync(product.BreedId, cancellationToken));
           
            Task.WhenAll(getCatInfoTask, getCatPriceHistoryTask);

            var catInfo = getCatInfoTask.Result;
            var catPriceHistory = getCatPriceHistoryTask.Result;
            var additionalCatInfo = await PollyHelper.ExecuteWithRetryAndFallBack(async () => await _additionalCatInfo.FindAsync(product.Id, cancellationToken));

            return BuildCat(product, catInfo, additionalCatInfo, catPriceHistory);
        }

        private async Task<List<Product>> GetProductsFromBillingServiceAsync(int skip, int limit, CancellationToken cancellationToken)
        {
            return await PollyHelper.ExecuteWithRetryAndFallBack(
                async () => await _billingService.GetProductsAsync(skip, limit, cancellationToken));
        }
        
        private async Task<CatInfo[]> GetCatsInfoFromCatInfoServiceAsync(Guid[] breedIds, CancellationToken cancellationToken)
        {
            CatInfo[] catsInfo;
            
            try
            {
                catsInfo = await PollyHelper.ExecuteWithRetryAndFallBack(
                    async () => await _catInfoService.FindByBreedIdAsync(breedIds, cancellationToken));
            }
            catch (InvalidRequestException)
            {
                catsInfo = Array.Empty<CatInfo>();
            }

            return catsInfo;
        }
        
        private async Task<Dictionary<Guid, CatPriceHistory>> GetCatsPriceHistoryFromCatExchangeServiceAsync(Guid[] breedIds, CancellationToken cancellationToken)
        {
            return await PollyHelper.ExecuteWithRetryAndFallBack(
                async () => await _catExchangeService.GetPriceInfoAsync(breedIds, cancellationToken));

        }
        
        private async Task<CatInfo> GetCatInfoFromCatInfoServiceAsync(string breed, CancellationToken cancellationToken)
        {
            return await PollyHelper.ExecuteWithRetryAndFallBack(
                async () => await _catInfoService.FindByBreedNameAsync(breed, cancellationToken));
        }
        
        private async Task AddProductToBillingServiceAsync(Product product, CancellationToken cancellationToken)
        {
            await PollyHelper.ExecuteWithRetryAndFallBack(
                    async () => await _billingService.AddProductAsync(product, cancellationToken));
        }
        
        private async Task<Product> GetProductFromBillingServiceAsync(Guid productId, CancellationToken cancellationToken)
        {
            var product = await PollyHelper.ExecuteWithRetryAndFallBack(
                async () => await _billingService.GetProductAsync(productId, cancellationToken));

            if (product == null)
            {
                throw new InvalidRequestException();
            }
            
            return product;
        }
        
        private async Task<Bill> SellProductOnBillingServiceAsync(Guid productId, decimal price, CancellationToken cancellationToken)
        {
            var bill = await PollyHelper.ExecuteWithRetryAndFallBack(
                async () => await _billingService.SellProductAsync(productId, price, cancellationToken));

            return bill;
        }
        
        private async Task DeleteCatFromFavouritesForEachUserAsync(Guid catId, CancellationToken cancellationToken)
        {
            var usersFavouriteCats = await _favouriteCats.FindAsync(user => user.CatsIds.Contains(catId), cancellationToken);
            
            foreach (var userFavouriteCats in usersFavouriteCats)
            {
                userFavouriteCats.CatsIds.Remove(catId);
                
                await _favouriteCats.WriteAsync(userFavouriteCats, cancellationToken);
            }
        }
        
        private async Task DeleteCatAdditionalInfoAsync(Guid catId, CancellationToken cancellationToken)
        {
            await _additionalCatInfo.DeleteAsync(catId, cancellationToken);
        }
        
        private Cat BuildCat(
            Product product, 
            CatInfo catInfo, 
            AdditionalCatInfo additionalCatInfo,
            CatPriceHistory catPriceHistory)
        {
            var cat = new Cat
            {
                Id = product.Id,
                BreedId = product.BreedId,
                AddedBy = additionalCatInfo?.AddedBy ?? Guid.Empty,
                Name = additionalCatInfo?.Name,
                CatPhoto = additionalCatInfo?.Photo
            };

            if (catInfo != null)
            {
                cat.Breed = catInfo.BreedName;
                cat.BreedPhoto = catInfo.Photo;
            }
            
            var (catPrices, price) = GetCatPriceHistoryAndCurrentPrice(catPriceHistory);
            cat.Prices = catPrices;
            cat.Price = price;
            
            return cat;
        }
        
        private (List<(DateTime Date, decimal Price)> CatPriceHistory, decimal Price) GetCatPriceHistoryAndCurrentPrice(CatPriceHistory catPriceHistory)
        {
            var catPrices = catPriceHistory.Prices?
                    .Select(p => (p.Date, p.Price))
                    .ToList()
                ?? new List<(DateTime Date, decimal Price)>();
                
            var catPrice = (catPrices.Count == 0) 
                ? 1000m 
                : catPrices.Last().Price;
            
            return (catPrices, catPrice);
        }
        
        private (List<(DateTime Date, decimal Price)> CatPriceHistory, decimal Price) GetCatPriceHistoryAndCurrentPrice(
            Guid breedId,
            Dictionary<Guid, CatPriceHistory> catsPriceHistory)
        {
            var catPrices = GetCatPriceHistory(breedId, catsPriceHistory);
            
            return GetCatPriceHistoryAndCurrentPrice(catPrices);
        }
        
        private CatPriceHistory GetCatPriceHistory(Guid breedId,  Dictionary<Guid, CatPriceHistory> catsPriceHistory)
        {
            return catsPriceHistory.ContainsKey(breedId)
                ? catsPriceHistory[breedId]
                : new CatPriceHistory();
        }
    }
}

namespace Microservices.Common
{
    public static class PollyHelper
    {
        /// <summary>
        /// Делает два вызова функции. Обрабатывает <see cref="ConnectionException"/>.
        /// Если оба вызова были неудачными генерирует <see cref="InternalErrorException"/>.
        /// </summary>
        /// <param name="call">Функция, которую следует вызвать</param>
        /// <returns>Результат вызова функции</returns>
        /// <exception cref="InternalErrorException"></exception>
        public static async Task<T> ExecuteWithRetryAndFallBack<T>(Func<Task<T>> call)
        {
            var fallbackPolicy = Policy<T>
                .Handle<ConnectionException>()
                .FallbackAsync(token => throw new InternalErrorException());
            
            var retryPolicy = Policy
                .Handle<ConnectionException>()
                .RetryAsync();
            
            var policy = fallbackPolicy.WrapAsync(retryPolicy);
            
            return await policy.ExecuteAsync(call);
        }
        
        public static async Task ExecuteWithRetryAndFallBack(Func<Task> call)
        {
            var fallbackPolicy = Policy
                .Handle<ConnectionException>()
                .FallbackAsync(token => throw new InternalErrorException());
            
            var retryPolicy = Policy
                .Handle<ConnectionException>()
                .RetryAsync();
            
            var policy = fallbackPolicy.WrapAsync(retryPolicy);

            await policy.ExecuteAsync(call);
        }
    }
}

namespace Microservices.ExternalServices.Database.Types
{
    /// <summary>
    /// Сущность для хранения избранных котиков пользователя
    /// </summary>
    public class UserFavouriteCats : IEntityWithId<Guid>
    {
        /// <summary>
        /// ИД пользователя
        /// </summary>
        public Guid Id { get; set; }
        
        /// <summary>
        /// ИД избранных котиков пользователя
        /// </summary>
        public List<Guid> CatsIds { get; set; } = new();
    }
    
    /// <summary>
    /// Сущность для хранения информации о котике, не принадлежащей другим сервисам
    /// </summary>
    public class AdditionalCatInfo : IEntityWithId<Guid>
    {
        /// <summary>
        /// ИД котика
        /// </summary>
        public Guid Id { get; set; }
        
        /// <summary>
        /// ИД пользователя, добавившего котика
        /// </summary>
        public Guid AddedBy { get; set; }
        
        /// <summary>
        /// Имя котика
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Фотография конкретного котика. При отсутствии равна null
        /// </summary>
        public byte[] Photo { get; set; }
    }
}