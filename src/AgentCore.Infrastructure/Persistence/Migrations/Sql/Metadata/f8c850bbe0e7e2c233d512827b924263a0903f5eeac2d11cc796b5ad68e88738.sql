
                CREATE TEMP TABLE DynamicModelLegacyGuard(Rows INTEGER CONSTRAINT LegacyModelDataResetRequired CHECK(Rows=0));
                INSERT INTO DynamicModelLegacyGuard SELECT
                    (SELECT count(*) FROM SessionSnapshots WHERE ModelCatalogKey IS NOT NULL OR SummaryModelCatalogKey IS NOT NULL OR ModelBindingJson IS NOT NULL OR SummaryModelBindingJson IS NOT NULL)
                    +(SELECT count(*) FROM ConversationEntries WHERE ModelCatalogKey IS NOT NULL OR ModelBindingJson IS NOT NULL)
                    +(SELECT count(*) FROM AgentRuns)
                    +(SELECT count(*) FROM TriggerOccurrences WHERE ModelCatalogKey IS NOT NULL OR ModelBindingJson IS NOT NULL)
                    +(SELECT count(*) FROM AgentInstances WHERE UnattendedModelCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM Automations WHERE ModelOverrideCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM (
                        SELECT DefinitionJson AS Document FROM Sessions
                        UNION ALL SELECT CandidateJson FROM AgentDefinitionDrafts
                        UNION ALL SELECT PayloadJson FROM AgentDefinitionPublications
                        UNION ALL SELECT SettingsOverridesJson FROM AgentInstances WHERE SettingsOverridesJson IS NOT NULL
                        UNION ALL SELECT PayloadJson FROM TenantApprovalRequests
                        UNION ALL SELECT PayloadJson FROM CoreEvents
                        UNION ALL SELECT PayloadJson FROM CoreEventBuckets
                    ) AS LegacyDocuments WHERE NOT json_valid(Document) OR EXISTS (
                        SELECT 1 FROM json_tree(CASE WHEN json_valid(Document) THEN Document ELSE '{}' END)
                        WHERE lower(key) = 'languagemodel'
                            OR (lower(key) IN ('catalogkey','provideralias','providerlanguagemodel','modelcatalogkey','unattendedmodelcatalogkey','modeloverridecatalogkey','modelkey','defaultmodelkey','effectivemodelkey'))
                    ));
                DROP TABLE DynamicModelLegacyGuard;
                