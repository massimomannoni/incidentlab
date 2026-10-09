# Input is an AWS get-parameter response. Emit only validated dotenv rows;
# failures never contain decrypted values. Invoked with --arg role monitor|target.
try (
  if .Parameter.Type != "SecureString" then error("invalid") else .Parameter.Value | fromjson end
  | if type != "object" then error("invalid") else . end
  | if $role == "monitor" then
      . + {LAB_BEDROCK_ENABLED: (.LAB_BEDROCK_ENABLED // "false"), LAB_BEDROCK_REGION: (.LAB_BEDROCK_REGION // "eu-south-1"),
           LAB_BEDROCK_MONTHLY_CAP_USD: (.LAB_BEDROCK_MONTHLY_CAP_USD // "5"), LAB_BEDROCK_DAILY_CAP_USD: (.LAB_BEDROCK_DAILY_CAP_USD // "0.50")}
      | if (.LAB_BEDROCK_ENABLED | IN("true", "false")) and .LAB_BEDROCK_REGION == "eu-south-1" and
           (.LAB_BEDROCK_MONTHLY_CAP_USD | tonumber | . >= 0 and . <= 100) and (.LAB_BEDROCK_DAILY_CAP_USD | tonumber | . >= 0 and . <= 0.50)
        then . else error("invalid") end
    else . end
  | (if $role == "monitor" then
       ["POSTGRES_PASSWORD", "LAB_INGEST_KEY", "LAB_CONTROL_KEY", "LAB_WORKER_KEY", "TARGET_URL", "MONITOR_BIND_ADDRESS", "LAB_BEDROCK_ENABLED", "LAB_BEDROCK_REGION", "LAB_BEDROCK_MONTHLY_CAP_USD", "LAB_BEDROCK_DAILY_CAP_USD"]
     elif $role == "target" then
       ["LAB_INGEST_KEY", "LAB_CONTROL_KEY", "MONITOR_URL", "TARGET_BIND_ADDRESS"]
     else error("invalid") end | sort) as $expected
  | if keys != $expected or any(.[]; type != "string") then error("invalid") else . end
  | if any(.[]; (test("^[A-Za-z0-9:/._-]+$")) | not) then error("invalid") else . end
  | to_entries | sort_by(.key) | .[] | "\(.key)=\(.value)"
) catch error("Invalid installation configuration (values withheld)")
