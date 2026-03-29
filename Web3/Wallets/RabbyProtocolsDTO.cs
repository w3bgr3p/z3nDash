namespace z3n8;

public class RootObject
{
    public string id { get; set; }
    public string chain { get; set; }
    public string name { get; set; }
    public string site_url { get; set; }
    public string logo_url { get; set; }
    public bool has_supported_portfolio { get; set; }
    public double tvl { get; set; }
    public Portfolio_item_list[] portfolio_item_list { get; set; }
}

public class Portfolio_item_list
{
    public Stats stats { get; set; }
    public Asset_dict asset_dict { get; set; }
    public Asset_token_list[] asset_token_list { get; set; }
    public Withdraw_actions[] withdraw_actions { get; set; }
    public int update_at { get; set; }
    public string name { get; set; }
    public string[] detail_types { get; set; }
    public Detail detail { get; set; }
    public Proxy_detail proxy_detail { get; set; }
    public Pool pool { get; set; }
    public string position_index { get; set; }
}

public class Stats
{
    public double asset_usd_value { get; set; }
    public double debt_usd_value { get; set; }
    public double net_usd_value { get; set; }
}

public class Asset_dict
{
   
}
public class Asset_token_list
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
    public bool is_verified { get; set; }
    public bool? is_core { get; set; }
    public bool is_wallet { get; set; }
    public int? time_at { get; set; }
    public double amount { get; set; }
    public double claimable_amount { get; set; }
}

public class Withdraw_actions
{
    public string type { get; set; }
    public string contract_id { get; set; }
    public string func { get; set; }
    public object[] _params { get; set; }
    public object[] str_params { get; set; }
    public Need_approve need_approve { get; set; }
}

public class Need_approve
{
    public string token_id { get; set; }
    public string to { get; set; }
    public long raw_amount { get; set; }
    public string str_raw_amount { get; set; }
}

public class Detail
{
    public Supply_token_list[] supply_token_list { get; set; }
    public double? health_rate { get; set; }
    public int unlock_at { get; set; }
    public Borrow_token_list[] borrow_token_list { get; set; }
    public Reward_token_list[] reward_token_list { get; set; }
    public string description { get; set; }
    public string side { get; set; }
    public Margin_token margin_token { get; set; }
    public Position_token position_token { get; set; }
    public Base_token base_token { get; set; }
    public Quote_token quote_token { get; set; }
    public int daily_funding_rate { get; set; }
    public double entry_price { get; set; }
    public double mark_price { get; set; }
    public double liquidation_price { get; set; }
    public double margin_rate { get; set; }
    public double pnl_usd_value { get; set; }
    public double leverage { get; set; }
    public Supply_nft_list[] supply_nft_list { get; set; }
    public Token token { get; set; }
    public int end_at { get; set; }
}

public class Supply_token_list
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
    public bool is_verified { get; set; }
    public bool? is_core { get; set; }
    public bool is_wallet { get; set; }
    public int? time_at { get; set; }
    public double amount { get; set; }
}

public class Borrow_token_list
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
    public bool is_verified { get; set; }
    public bool is_core { get; set; }
    public bool is_wallet { get; set; }
    public int time_at { get; set; }
    public double amount { get; set; }
}

public class Reward_token_list
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
    public bool is_verified { get; set; }
    public bool is_core { get; set; }
    public bool is_wallet { get; set; }
    public int? time_at { get; set; }
    public double amount { get; set; }
}

public class Margin_token
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
    public bool is_verified { get; set; }
    public bool is_core { get; set; }
    public bool is_wallet { get; set; }
    public int time_at { get; set; }
    public double amount { get; set; }
}

public class Position_token
{
    public string id { get; set; }
    public string chain { get; set; }
    public string name { get; set; }
    public string symbol { get; set; }
    public object display_symbol { get; set; }
    public string optimized_symbol { get; set; }
    public int decimals { get; set; }
    public string logo_url { get; set; }
    public string protocol_id { get; set; }
    public double price { get; set; }
    public bool is_verified { get; set; }
    public object is_core { get; set; }
    public bool is_wallet { get; set; }
    public int time_at { get; set; }
    public double amount { get; set; }
}

public class Base_token
{
    public string id { get; set; }
    public string chain { get; set; }
    public string name { get; set; }
    public string symbol { get; set; }
    public object display_symbol { get; set; }
    public string optimized_symbol { get; set; }
    public int decimals { get; set; }
    public string logo_url { get; set; }
    public string protocol_id { get; set; }
    public double price { get; set; }
    public bool is_verified { get; set; }
    public object is_core { get; set; }
    public bool is_wallet { get; set; }
    public int time_at { get; set; }
}

public class Quote_token
{
    public string id { get; set; }
    public string chain { get; set; }
    public string name { get; set; }
    public string symbol { get; set; }
    public object display_symbol { get; set; }
    public string optimized_symbol { get; set; }
    public int decimals { get; set; }
    public string logo_url { get; set; }
    public string protocol_id { get; set; }
    public double price { get; set; }
    public bool is_verified { get; set; }
    public object is_core { get; set; }
    public bool is_wallet { get; set; }
    public int time_at { get; set; }
}

public class Supply_nft_list
{
    public string id { get; set; }
    public string chain { get; set; }
    public string contract_id { get; set; }
    public string inner_id { get; set; }
    public string name { get; set; }
    public string content_url { get; set; }
    public string thumbnail_url { get; set; }
    public Collection collection { get; set; }
    public int amount { get; set; }
}

public class Collection
{
    public string chain_id { get; set; }
    public string id { get; set; }
    public string name { get; set; }
    public string symbol { get; set; }
    public string logo_url { get; set; }
    public int is_core { get; set; }
}

public class Token
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
    public int price { get; set; }
    public bool is_verified { get; set; }
    public bool is_core { get; set; }
    public bool is_wallet { get; set; }
    public int time_at { get; set; }
    public double amount { get; set; }
    public double claimable_amount { get; set; }
}

public class Proxy_detail
{
    public Project project { get; set; }
    public string proxy_contract_id { get; set; }
}

public class Project
{
    public string id { get; set; }
    public string name { get; set; }
    public string site_url { get; set; }
    public string logo_url { get; set; }
}

public class Pool
{
    public string id { get; set; }
    public string chain { get; set; }
    public string project_id { get; set; }
    public string adapter_id { get; set; }
    public string controller { get; set; }
    public string index { get; set; }
    public int time_at { get; set; }
}

