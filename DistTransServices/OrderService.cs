﻿using DistTransDto;
using DistTransServices.DbContexts;
using DistTransServices.Entitys;
using PWMIS.Core.Extensions;
using PWMIS.DataProvider.Data;
using PWMIS.EnterpriseFramework.Common;
using PWMIS.EnterpriseFramework.Service.Basic;
using PWMIS.EnterpriseFramework.Service.Client;
using PWMIS.EnterpriseFramework.Service.Runtime;
//using PWMIS.EnterpriseFramework.Service.Basic;
//using PWMIS.EnterpriseFramework.Service.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DistTransServices
{
    public class OrderService:ServiceBase
    {
        Proxy productProxy;
        Proxy DTS_Proxy;
        public OrderService()
        {
            productProxy = new Proxy();
            productProxy.ServiceBaseUri = System.Configuration.ConfigurationManager.AppSettings["ProductUri"];

            DTS_Proxy = new Proxy();
            DTS_Proxy.ServiceBaseUri = System.Configuration.ConfigurationManager.AppSettings["MSF_DTS_Uri"];
        }

        /// <summary>
        /// 生成订单的服务方法
        /// </summary>
        /// <param name="orderId">订单号</param>
        /// <param name="userId">用户号</param>
        /// <param name="buyItems">购买的商品简要清单</param>
        /// <returns>订单是否创建成功</returns>
        public bool CreateOrder(int orderId,int userId,IEnumerable<BuyProductDto> buyItems)
        {
            //在分布式事务的发起端，需要先定义分布式事务标识：
            string DT_Identity = System.Guid.NewGuid().ToString();
            productProxy.RegisterData = DT_Identity;

            //使用3阶段提交的分布式事务，保存订单到数据库
            OrderDbContext context = new OrderDbContext();

            DTController controller = new DTController(DT_Identity);
            return controller.DistTrans3PCRequest<bool>(DTS_Proxy, 
                context.CurrentDataBase,
                db =>
                {
                    //先请求商品服务，扣减库存，并获取商品的仓库信息
                    ServiceRequest request = new ServiceRequest();
                    request.ServiceName = "ProductService";
                    request.MethodName = "UpdateProductOnhand";
                    request.Parameters = new object[] { DT_Identity, buyItems };
                    List<SellProductDto> sellProducts = productProxy.RequestServiceAsync<List<SellProductDto>>(request).Result;

                    #region 构造订单明细和订单对象
                    //
                    productProxy.Connect();
                    List<OrderItemEntity> orderItems = new List<OrderItemEntity>();
                    OrderEntity order = new OrderEntity()
                    {
                        ID = orderId,
                        OwnerID = userId,
                        OrderTime = DateTime.Now,
                        OrderName = "Prudoct:"
                    };
                    foreach (BuyProductDto item in buyItems)
                    {
                        //注意：在商品数据库上，前面更新商品，但还没有提交事务，下面这个查询直接使用的话会导致查询等待，因为SQLSERVER的事务隔离级别是这样的
                        //所以 GetProductInfo 的实现需要注意。
                        //ProductDto product = this.GetProductInfo(item.ProductId).Result;
                        ProductDto product = this.GetProductInfoSync(item.ProductId);

                        OrderItemEntity temp = new OrderItemEntity()
                        {
                            OrderID = orderId,
                            ProductID = product.ID,
                            BuyNumber = item.BuyNumber,
                            OnePrice = product.Price,
                            ProductName = product.ProductName
                        };
                        temp.StoreHouse = (from i in sellProducts where i.ProductId == temp.ProductID select i.StoreHouse).FirstOrDefault();

                        orderItems.Add(temp);
                        order.OrderName += "," + temp.ProductName;
                        order.AmountPrice += temp.OnePrice * temp.BuyNumber;
                    }
                    //
                    //关闭商品服务订阅者连接
                    productProxy.Close();

                    #endregion

                    //保存订单数据到数据库
                    context.Add<OrderEntity>(order);
                    context.AddList<OrderItemEntity>(orderItems);
                    return true;
                });
        }

        /// <summary>
        /// 从商品服务，获取商品信息。这种方式会使用新的连接
        /// </summary>
        /// <param name="productId"></param>
        /// <returns></returns>
        private async Task<ProductDto> GetProductInfo(int productId)
        {
            ServiceRequest request = new ServiceRequest();
            request.ServiceName = "ProductService";
            request.MethodName = "GetProductInfo";
            request.Parameters = new object[] { productId};
            return await productProxy.RequestServiceAsync<ProductDto>(request);
            
        }

        /// <summary>
        /// （同步方式）从商品服务，获取商品信息。这种方式会使用之前打开的连接
        /// </summary>
        /// <param name="productId"></param>
        /// <returns></returns>
        private ProductDto GetProductInfoSync(int productId)
        {
            ServiceRequest request = new ServiceRequest();
            request.ServiceName = "ProductService";
            request.MethodName = "GetProductInfo";
            request.Parameters = new object[] { productId };
            return  productProxy.GetServiceMessage<ProductDto>(request, DataType.Json).Result;

        }

        private async Task<bool> UpdateProductOnhand(IEnumerable<BuyProductDto> buyItems)
        {
            ServiceRequest request = new ServiceRequest();
            request.ServiceName = "ProductService";
            request.MethodName = "UpdateProductOnhand";
            request.Parameters = new object[] { buyItems };
            return await productProxy.RequestServiceAsync<bool>(request);
        }
    }
}
