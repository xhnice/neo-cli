using Neo.Core;
using Neo.Implementations.Wallets.NEP6;
using Neo.IO;
using Neo.IO.Json;
using Neo.SmartContract;
using Neo.Wallets;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Neo.Network.RPC
{
    internal class RpcServerWithWallet : RpcServer
    {
        public RpcServerWithWallet(LocalNode localNode)
            : base(localNode)
        {
        }

        protected override JObject Process(string method, JArray _params)
        {
            switch (method)
            {
                case "":
                case "getapplicationlog":
                    {
                        UInt256 hash = UInt256.Parse(_params[0].AsString());
                        string path = Path.Combine(Settings.Default.Paths.ApplicationLogs, $"{hash}.json");
                        return File.Exists(path)
                            ? JObject.Parse(File.ReadAllText(path))
                            : throw new RpcException(-100, "Unknown transaction");
                    }
                case "balance": // 使用address查询余额 AddCode
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied.");
                    else
                    {
                        JObject json = new JObject();
                        if (_params.Count() == 1)
                        {
                            // 查询NEO和GAS资产
                            string address = _params[0].AsString();
                            try
                            {
                                Wallet.ToScriptHash(address);
                            } catch
                            {
                                json["code"] = -1;
                                json["message"] = "NEO地址错误";
                                return json;
                            }
                            IEnumerable<Coin> coins = Program.Wallet.GetCoins(address);
                            // NEO
                            var neoCoins = coins.Where(p => !p.State.HasFlag(CoinState.Spent) && p.Output.AssetId.Equals(UInt256.Parse(Settings.Default.Asset.NEOAsset)));
                            json["balance"] = neoCoins.Sum(p => p.Output.Value).ToString();
                            json["confirmed"] = neoCoins.Where(p => p.State.HasFlag(CoinState.Confirmed)).Sum(p => p.Output.Value).ToString();
                            // GAS
                            var gasCoins = coins.Where(p => !p.State.HasFlag(CoinState.Spent) && p.Output.AssetId.Equals(UInt256.Parse(Settings.Default.Asset.GASAsset)));
                            json["gasbalance"] = gasCoins.Sum(p => p.Output.Value).ToString();
                            json["gasconfirmed"] = gasCoins.Where(p => p.State.HasFlag(CoinState.Confirmed)).Sum(p => p.Output.Value).ToString();
                            return json;
                        }
                        else // 查询指定资产余额
                        {
                            string address = _params[1].AsString();
                            
                            switch (UIntBase.Parse(_params[0].AsString()))
                            {
                                case UInt160 asset_id_160: //NEP-5 balance
                                    json["balance"] = Program.Wallet.GetAvailable(asset_id_160, address).ToString();
                                    break;
                                case UInt256 asset_id_256: //Global Assets balance
                                    IEnumerable<Coin> coins = Program.Wallet.GetCoins(address).Where(p => !p.State.HasFlag(CoinState.Spent) && p.Output.AssetId.Equals(asset_id_256));
                                    json["balance"] = coins.Sum(p => p.Output.Value).ToString();
                                    json["confirmed"] = coins.Where(p => p.State.HasFlag(CoinState.Confirmed)).Sum(p => p.Output.Value).ToString();
                                    break;
                            }
                            return json;
                        }
                       
                    }
                case "getbalance":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied.");
                    else
                    {
                        JObject json = new JObject();
                        switch (UIntBase.Parse(_params[0].AsString()))
                        {
                            case UInt160 asset_id_160: //NEP-5 balance
                                json["balance"] = Program.Wallet.GetAvailable(asset_id_160).ToString();
                                break;
                            case UInt256 asset_id_256: //Global Assets balance
                                IEnumerable<Coin> coins = Program.Wallet.GetCoins().Where(p => !p.State.HasFlag(CoinState.Spent) && p.Output.AssetId.Equals(asset_id_256));
                                json["balance"] = coins.Sum(p => p.Output.Value).ToString();
                                json["confirmed"] = coins.Where(p => p.State.HasFlag(CoinState.Confirmed)).Sum(p => p.Output.Value).ToString();
                                break;
                        }
                        return json;
                    }
                case "listaddress":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied.");
                    else
                        return Program.Wallet.GetAccounts().Select(p =>
                        {
                            JObject account = new JObject();
                            account["address"] = p.Address;
                            account["haskey"] = p.HasKey;
                            account["label"] = p.Label;
                            account["watchonly"] = p.WatchOnly;
                            return account;
                        }).ToArray();
                case "sendfrom":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        UIntBase assetId = UIntBase.Parse(_params[0].AsString());
                        AssetDescriptor descriptor = new AssetDescriptor(assetId);
                        UInt160 from = Wallet.ToScriptHash(_params[1].AsString());
                        UInt160 to = Wallet.ToScriptHash(_params[2].AsString());
                        BigDecimal value = BigDecimal.Parse(_params[3].AsString(), descriptor.Decimals);
                        if (value.Sign <= 0)
                            throw new RpcException(-32602, "Invalid params");
                        Fixed8 fee = _params.Count >= 5 ? Fixed8.Parse(_params[4].AsString()) : Fixed8.Zero;
                        if (fee < Fixed8.Zero)
                            throw new RpcException(-32602, "Invalid params");
                        UInt160 change_address = _params.Count >= 6 ? Wallet.ToScriptHash(_params[5].AsString()) : null;
                        Transaction tx = Program.Wallet.MakeTransaction(null, new[]
                        {
                            new TransferOutput
                            {
                                AssetId = assetId,
                                Value = value,
                                ScriptHash = to
                            }
                        }, from: from, change_address: change_address, fee: fee);
                        if (tx == null)
                            throw new RpcException(-300, "Insufficient funds");
                        ContractParametersContext context = new ContractParametersContext(tx);
                        Program.Wallet.Sign(context);
                        if (context.Completed)
                        {
                            tx.Scripts = context.GetScripts();
                            Program.Wallet.ApplyTransaction(tx);
                            LocalNode.Relay(tx);
                            return tx.ToJson();
                        }
                        else
                        {
                            return context.ToJson();
                        }
                    }
                case "sendfromto": // 从指定账号向指定账号转账 AddCode
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        // 参数顺序  资产类型  输出账号  输入账号  输出金额  输出账号私钥  手续费    找零地址
                        UIntBase assetId = UIntBase.Parse(_params[0].AsString());
                        AssetDescriptor descriptor = new AssetDescriptor(assetId);
                        UInt160 from = Wallet.ToScriptHash(_params[1].AsString());
                        UInt160 to = Wallet.ToScriptHash(_params[2].AsString());
                        BigDecimal value = BigDecimal.Parse(_params[3].AsString(), descriptor.Decimals);
                        string privatekey = _params[4].AsString();
                        if (value.Sign <= 0)
                            throw new RpcException(-32602, "Invalid params");
                        Fixed8 fee = _params.Count >= 6 ? Fixed8.Parse(_params[5].AsString()) : Fixed8.Zero;
                        if (fee < Fixed8.Zero)
                            throw new RpcException(-32602, "Invalid params");
                        UInt160 change_address = _params.Count >= 7 && !string.IsNullOrEmpty(_params[6].AsString()) ? Wallet.ToScriptHash(_params[6].AsString()) : from;// 找零地址 
                        Transaction tx = Program.Wallet.MakeTransaction(null, new[]
                        {
                            new TransferOutput
                            {
                                AssetId = assetId,
                                Value = value,
                                ScriptHash = to
                            }
                        }, from: from, change_address: change_address, fee: fee);
                        if (tx == null)
                            throw new RpcException(-300, "Insufficient funds");
                        ContractParametersContext context = new ContractParametersContext(tx);
                        //File.AppendAllText("wallet.log", context.ScriptHashes.Count().ToString());
                        //Program.Wallet.Sign(context);
                        Program.Wallet.Sign(context, privatekey);
                        if (context.Completed)
                        {
                            tx.Scripts = context.GetScripts();
                            Program.Wallet.ApplyTransaction(tx);
                            LocalNode.Relay(tx);
                            return tx.ToJson();
                        }
                        else
                        {
                            return context.ToJson();
                        }
                    }
                case "sendtoaddress":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        UIntBase assetId = UIntBase.Parse(_params[0].AsString());
                        AssetDescriptor descriptor = new AssetDescriptor(assetId);
                        UInt160 scriptHash = Wallet.ToScriptHash(_params[1].AsString());
                        BigDecimal value = BigDecimal.Parse(_params[2].AsString(), descriptor.Decimals);
                        if (value.Sign <= 0)
                            throw new RpcException(-32602, "Invalid params");
                        Fixed8 fee = _params.Count >= 4 ? Fixed8.Parse(_params[3].AsString()) : Fixed8.Zero;
                        if (fee < Fixed8.Zero)
                            throw new RpcException(-32602, "Invalid params");
                        UInt160 change_address = _params.Count >= 5 ? Wallet.ToScriptHash(_params[4].AsString()) : null;
                        Transaction tx = Program.Wallet.MakeTransaction(null, new[]
                        {
                            new TransferOutput
                            {
                                AssetId = assetId,
                                Value = value,
                                ScriptHash = scriptHash
                            }
                        }, change_address: change_address, fee: fee);
                        if (tx == null)
                            throw new RpcException(-300, "Insufficient funds");
                        ContractParametersContext context = new ContractParametersContext(tx);
                        Program.Wallet.Sign(context);
                        if (context.Completed)
                        {
                            tx.Scripts = context.GetScripts();
                            Program.Wallet.ApplyTransaction(tx);
                            LocalNode.Relay(tx);
                            return tx.ToJson();
                        }
                        else
                        {
                            return context.ToJson();
                        }
                    }
                case "sendmany":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        JArray to = (JArray)_params[0];
                        if (to.Count == 0)
                            throw new RpcException(-32602, "Invalid params");
                        TransferOutput[] outputs = new TransferOutput[to.Count];
                        for (int i = 0; i < to.Count; i++)
                        {
                            UIntBase asset_id = UIntBase.Parse(to[i]["asset"].AsString());
                            AssetDescriptor descriptor = new AssetDescriptor(asset_id);
                            outputs[i] = new TransferOutput
                            {
                                AssetId = asset_id,
                                Value = BigDecimal.Parse(to[i]["value"].AsString(), descriptor.Decimals),
                                ScriptHash = Wallet.ToScriptHash(to[i]["address"].AsString())
                            };
                            if (outputs[i].Value.Sign <= 0)
                                throw new RpcException(-32602, "Invalid params");
                        }
                        Fixed8 fee = _params.Count >= 2 ? Fixed8.Parse(_params[1].AsString()) : Fixed8.Zero;
                        if (fee < Fixed8.Zero)
                            throw new RpcException(-32602, "Invalid params");
                        UInt160 change_address = _params.Count >= 3 ? Wallet.ToScriptHash(_params[2].AsString()) : null;
                        Transaction tx = Program.Wallet.MakeTransaction(null, outputs, change_address: change_address, fee: fee);
                        if (tx == null)
                            throw new RpcException(-300, "Insufficient funds");
                        ContractParametersContext context = new ContractParametersContext(tx);
                        Program.Wallet.Sign(context);
                        if (context.Completed)
                        {
                            tx.Scripts = context.GetScripts();
                            Program.Wallet.ApplyTransaction(tx);
                            LocalNode.Relay(tx);
                            return tx.ToJson();
                        }
                        else
                        {
                            return context.ToJson();
                        }
                    }
                case "getnewaddress":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        WalletAccount account = Program.Wallet.CreateAccount();
                        if (Program.Wallet is NEP6Wallet wallet)
                            wallet.Save();
                        return account.Address;
                    }
                case "newaddress":// 创建一个账号 AddCode
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        WalletAccount account = Program.Wallet.CreateAccount();
                        if (Program.Wallet is NEP6Wallet wallet)
                            wallet.Save();
                        return account.OutputJson();
                    }
                case "dumpprivkey":
                    if (Program.Wallet == null)
                        throw new RpcException(-400, "Access denied");
                    else
                    {
                        UInt160 scriptHash = Wallet.ToScriptHash(_params[0].AsString());
                        WalletAccount account = Program.Wallet.GetAccount(scriptHash);
                        return account.GetKey().Export();
                    }
                case "invoke":// 使用给定的参数以散列值调用智能合约
                case "invokefunction":
                case "invokescript":
                    JObject result = base.Process(method, _params);
                    if (Program.Wallet != null)
                    {
                        InvocationTransaction tx = new InvocationTransaction
                        {
                            Version = 1,
                            Script = result["script"].AsString().HexToBytes(),
                            Gas = Fixed8.Parse(result["gas_consumed"].AsString())
                        };
                        tx.Gas -= Fixed8.FromDecimal(10);
                        if (tx.Gas < Fixed8.Zero) tx.Gas = Fixed8.Zero;
                        tx.Gas = tx.Gas.Ceiling();
                        tx = Program.Wallet.MakeTransaction(tx);
                        if (tx != null)
                        {
                            ContractParametersContext context = new ContractParametersContext(tx);
                            Program.Wallet.Sign(context);
                            if (context.Completed)
                                tx.Scripts = context.GetScripts();
                            else
                                tx = null;
                        }
                        result["tx"] = tx?.ToArray().ToHexString();
                    }
                    return result;
                default:
                    return base.Process(method, _params);
            }
        }
    }
}
