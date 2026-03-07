namespace z3n8;

public class ChainsTotal
{
    public double total_usd_value { get; set; }
    public Chains[] chain_list { get; set; }
    public int error_code { get; set; }
}

public class Chains
{
    public string id { get; set; }
    public int community_id { get; set; }
    public string name { get; set; }
    public string native_token_id { get; set; }
    public string logo_url { get; set; }
    public string wrapped_token_id { get; set; }
    public int? born_at { get; set; }
    public double usd_value { get; set; }
}

public class Tokens
{
    public string id { get; set; }
    public string chain { get; set; }
    public string name { get; set; }
    public string symbol { get; set; }
    public string display_symbol { get; set; }
    public string optimized_symbol { get; set; }
    public int decimals { get; set; }
    public string logo_url { get; set; }
    public string protocol_id { get; set; }
    public double price { get; set; }
    public double? price_24h_change { get; set; }
    public double credit_score { get; set; }
    public double total_supply { get; set; }
    public bool is_verified { get; set; }
    public bool is_core { get; set; }
    public bool is_wallet { get; set; }
    public bool is_scam { get; set; }
    public bool is_suspicious { get; set; }
    public int? time_at { get; set; }
    public double amount { get; set; }
    public double raw_amount { get; set; }
    public string raw_amount_hex_str { get; set; }
    public string raw_amount_str { get; set; }
    public string[] cex_ids { get; set; }
    public double? fdv { get; set; }
}

