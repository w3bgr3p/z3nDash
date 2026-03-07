

using System;
using System.Collections.Generic;


    public enum RpcUrl
    {
        Arbitrum = 42161,
        Base = 8453,
        Blast = 81457,
        Optimism = 10,
        Linea = 59144,
        Ethereum = 1,          
        Scroll = 534352,
        Soneium = 1868,
        Taiko = 167000,
        Unichain = 1301,
        Zero = 543210,
        Zora = 7777777,
        Zksync = 324,
        
    
        Solana = 900001,
        Solana_Devnet = 900002,
        Solana_Testnet = 900003,
        Aptos = 900004,
    
        // Testnets
        Sepolia = 11155111,
        MonadTestnet = 41454,
        NeuraTestnet = 999999,
    
        // Другие сети
        Avalanche = 43114,
        
        Bsc = 56,
        Opbnb = 204,
        Gnosis = 100,
        Fantom = 250,
        Manta = 169,
        Mantle = 5000,
        Polygon = 137,
        Gravity = 1625,
        
        
    }

    public static class Rpc
    {
        private static readonly Dictionary<RpcUrl, string> _rpcs = new Dictionary<RpcUrl, string>
        {
            {RpcUrl.Ethereum, "https://ethereum-rpc.publicnode.com"},
            {RpcUrl.Arbitrum, "https://arbitrum-one.publicnode.com"},
            {RpcUrl.Base, "https://base-rpc.publicnode.com"},
            {RpcUrl.Blast, "https://rpc.blast.io"},
            {RpcUrl.Fantom, "https://rpc.fantom.network"},
            {RpcUrl.Linea, "https://rpc.linea.build"},
            {RpcUrl.Manta, "https://pacific-rpc.manta.network/http"},
            {RpcUrl.Optimism, "https://optimism-rpc.publicnode.com"},
            {RpcUrl.Scroll, "https://rpc.scroll.io"},
            {RpcUrl.Soneium, "https://rpc.soneium.org"},
            {RpcUrl.Taiko, "https://rpc.mainnet.taiko.xyz"},
            {RpcUrl.Unichain, "https://unichain.drpc.org"},
            {RpcUrl.Zero, "https://zero.drpc.org"},
            {RpcUrl.Zksync, "https://mainnet.era.zksync.io"},
            {RpcUrl.Zora, "https://rpc.zora.energy"},
            
            
            {RpcUrl.Avalanche, "https://avalanche-c-chain.publicnode.com"},
            {RpcUrl.Bsc, "https://bsc-rpc.publicnode.com"},
            {RpcUrl.Gravity, "https://rpc.gravity.xyz"},
            {RpcUrl.Gnosis, "https://rpc.gnosischain.com"},
            {RpcUrl.Opbnb, "https://opbnb-mainnet-rpc.bnbchain.org"},
            {RpcUrl.Polygon, "https://polygon-rpc.com"},
            {RpcUrl.Mantle, "https://rpc.mantle.xyz"},
            
            
            {RpcUrl.Sepolia, "https://eth-sepolia.api.onfinality.io/public"},
            {RpcUrl.MonadTestnet, "https://testnet-rpc.monad.xyz"},
            {RpcUrl.Aptos, "https://fullnode.mainnet.aptoslabs.com/v1"},
            {RpcUrl.NeuraTestnet, "https://testnet.rpc.neuraprotocol.io"},
            {RpcUrl.Solana, "https://api.mainnet-beta.solana.com"},
            {RpcUrl.Solana_Devnet, "https://api.devnet.solana.com"},
            {RpcUrl.Solana_Testnet, "https://api.testnet.solana.com"}
        };

        public static string Get(RpcUrl network) => _rpcs[network];

        public static int ChainId(string name)
        {
            if (Enum.TryParse<RpcUrl>(name, true, out var network))
                return (int)network; // Приводим enum к int
    
            var normalized = name.Replace("_", "").Trim();
            foreach (RpcUrl net in Enum.GetValues(typeof(RpcUrl)))
            {
                if (string.Equals(net.ToString().Replace("_", ""), normalized, StringComparison.OrdinalIgnoreCase))
                    return (int)net; // Приводим enum к int
            }
    
            throw new ArgumentException($"No RpcUrl provided for '{name}'");
        }


        public static string Get(string name)
        {
            if (Enum.TryParse<RpcUrl>(name, true, out var network))
                return _rpcs[network];
            
            var normalized = name.Replace("_", "").Trim();
            foreach (RpcUrl net in Enum.GetValues(typeof(RpcUrl)))
            {
                if (string.Equals(net.ToString().Replace("_", ""), normalized, StringComparison.OrdinalIgnoreCase))
                    return _rpcs[net];
            }
            
            throw new ArgumentException($"No RpcUrl provided for '{name}'");
        }

        // Удобные статические свойства
        public static string Ethereum => _rpcs[RpcUrl.Ethereum];
        public static string Arbitrum => _rpcs[RpcUrl.Arbitrum];
        public static string Base => _rpcs[RpcUrl.Base];
        public static string Blast => _rpcs[RpcUrl.Blast];
        public static string Fantom => _rpcs[RpcUrl.Fantom];
        public static string Linea => _rpcs[RpcUrl.Linea];
        public static string Manta => _rpcs[RpcUrl.Manta];
        public static string Optimism => _rpcs[RpcUrl.Optimism];
        public static string Scroll => _rpcs[RpcUrl.Scroll];
        public static string Soneium => _rpcs[RpcUrl.Soneium];
        public static string Taiko => _rpcs[RpcUrl.Taiko];
        public static string Unichain => _rpcs[RpcUrl.Unichain];
        public static string Zero => _rpcs[RpcUrl.Zero];
        public static string Zksync => _rpcs[RpcUrl.Zksync];
        public static string Zora => _rpcs[RpcUrl.Zora];
        public static string Avalanche => _rpcs[RpcUrl.Avalanche];
        public static string Bsc => _rpcs[RpcUrl.Bsc];
        public static string Gravity => _rpcs[RpcUrl.Gravity];
        public static string Opbnb => _rpcs[RpcUrl.Opbnb];
        public static string Polygon => _rpcs[RpcUrl.Polygon];
        public static string Sepolia => _rpcs[RpcUrl.Sepolia];
        public static string Aptos => _rpcs[RpcUrl.Aptos];
        public static string Solana => _rpcs[RpcUrl.Solana];
        public static string Solana_Devnet => _rpcs[RpcUrl.Solana_Devnet];
        public static string Solana_Testnet => _rpcs[RpcUrl.Solana_Testnet];
        public static string Gnosis => _rpcs[RpcUrl.Gnosis];
        
        //testnets
        public static string NeuraTestnet => _rpcs[RpcUrl.NeuraTestnet];
        public static string MonadTestnet => _rpcs[RpcUrl.MonadTestnet];
    }

    // Использование:
    // Rpc.Get(RpcUrl.Ethereum)
    // Rpc.Get("ethereum")
    // Rpc.Ethereum

