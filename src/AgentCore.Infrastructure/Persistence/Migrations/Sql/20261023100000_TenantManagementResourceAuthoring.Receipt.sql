ManagementResourceJson IS NULL OR COALESCE((Outcome=0 AND ReasonCode='management_mutation_completed' AND (RequestId IS NOT NULL AND SourceCallId IS NULL AND ParametersHash IS NULL
 OR RequestId IS NULL AND SourceCallId IS NOT NULL AND ParametersHash IS NOT NULL AND length(SourceCallId) BETWEEN 1 AND 200
 AND length(ParametersHash)=64 AND ParametersHash NOT GLOB '*[^0-9a-f]*' AND json_extract(ManagementResourceJson,'$.ToolName')='resources.upsert'
 AND json_array_length(ManagementResourceJson,'$.BeforeSources')=0 AND json_array_length(ManagementResourceJson,'$.AfterSources')=1
 AND json_extract(ManagementResourceJson,'$.Result.Revision')=1 AND json_extract(ManagementResourceJson,'$.Result.Key') GLOB 'instance:*'
 AND json_extract(ManagementResourceJson,'$.Result.MediaType') IN ('text/plain','text/markdown','application/json') AND json_extract(ManagementResourceJson,'$.Result.ByteLength')<=128000)
AND ManagementSkillJson IS NULL AND AutomationMutationJson IS NULL AND BrowserArtifactsJson IS NULL
AND length(CAST(ManagementResourceJson AS BLOB)) BETWEEN 2 AND 12000 AND json_valid(ManagementResourceJson) AND json_type(ManagementResourceJson)='object'
AND json_extract(ManagementResourceJson,'$.ToolName') IN ('resources.upsert','resources.copy','resources.set_enabled','resources.delete')
AND json_type(ManagementResourceJson,'$.BeforeInstanceRevision')='integer' AND json_extract(ManagementResourceJson,'$.BeforeInstanceRevision')>=1 AND InstanceRevision=json_extract(ManagementResourceJson,'$.BeforeInstanceRevision')+1
AND json_type(ManagementResourceJson,'$.BeforeStableHash')='text' AND length(json_extract(ManagementResourceJson,'$.BeforeStableHash'))=64 AND json_extract(ManagementResourceJson,'$.BeforeStableHash') NOT GLOB '*[^0-9a-f]*'
AND json_type(ManagementResourceJson,'$.AfterStableHash')='text' AND length(json_extract(ManagementResourceJson,'$.AfterStableHash'))=64 AND json_extract(ManagementResourceJson,'$.AfterStableHash') NOT GLOB '*[^0-9a-f]*'
AND json_type(ManagementResourceJson,'$.BeforeSources')='array' AND json_array_length(ManagementResourceJson,'$.BeforeSources')<=2
AND json_type(ManagementResourceJson,'$.AfterSources')='array' AND json_array_length(ManagementResourceJson,'$.AfterSources')<=2
AND json_type(ManagementResourceJson,'$.Result')='object' AND json_type(ManagementResourceJson,'$.Result.Key')='text'
AND ((json_extract(ManagementResourceJson,'$.Result.Key') GLOB 'instance:*' AND length(json_extract(ManagementResourceJson,'$.Result.Key'))=45 AND json_extract(ManagementResourceJson,'$.Result.Revision')>=1)
 OR (json_extract(ManagementResourceJson,'$.Result.Key') GLOB 'definition:*' AND length(json_extract(ManagementResourceJson,'$.Result.Key'))=47 AND json_extract(ManagementResourceJson,'$.Result.Revision')>=0))
AND json_type(ManagementResourceJson,'$.Result.Revision')='integer' AND json_type(ManagementResourceJson,'$.Result.Enabled') IN ('true','false')
AND json_type(ManagementResourceJson,'$.Result.EnabledOverride') IN ('true','false','null') AND json_extract(ManagementResourceJson,'$.Result.Kind') IN (1,2,3,4)
AND json_type(ManagementResourceJson,'$.Result.LogicalPath')='text' AND length(json_extract(ManagementResourceJson,'$.Result.LogicalPath')) BETWEEN 1 AND 240
AND json_type(ManagementResourceJson,'$.Result.MediaType')='text' AND json_type(ManagementResourceJson,'$.Result.ContentSha256')='text'
AND length(json_extract(ManagementResourceJson,'$.Result.ContentSha256'))=64 AND json_extract(ManagementResourceJson,'$.Result.ContentSha256') NOT GLOB '*[^0-9a-f]*'
AND json_type(ManagementResourceJson,'$.Result.ByteLength')='integer' AND json_extract(ManagementResourceJson,'$.Result.ByteLength') BETWEEN 0 AND 8388608
AND (json_extract(ManagementResourceJson,'$.ToolName')='resources.delete' AND json_extract(ManagementResourceJson,'$.Result.Deleted')=1 AND json_extract(ManagementResourceJson,'$.Result.Key') GLOB 'instance:*'
 OR json_extract(ManagementResourceJson,'$.ToolName')<>'resources.delete' AND json_extract(ManagementResourceJson,'$.Result.Deleted')=0)),0)