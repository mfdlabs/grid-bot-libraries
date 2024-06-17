local assetUrl, fileExtension, x, y, baseUrl = ...

local ThumbnailGenerator = game:GetService("ThumbnailGenerator")

pcall(function() game:GetService("ContentProvider"):SetBaseUrl(baseUrl) end)
game:GetService("ScriptContext").ScriptsDisabled = true
game:DefineFastFlag("OnlyAllowMeshParts", false)

for _, object in pairs(game:GetObjects(assetUrl)) do
	if game:GetFastFlag("OnlyAllowMeshParts") then
		if object:IsA("MeshPart") and #object:GetChildren() == 0 then
			pcall(function() object.Parent = workspace end)
			break
		end
	else
		if object:IsA("Sky") then
			local resultValues = nil
			local success = pcall(function() resultValues = {ThumbnailGenerator:ClickTexture(object.SkyboxFt, fileExtension, x, y)} end)
			if success then
				return unpack(resultValues)
			else
				object.Parent = game:GetService("Lighting")
				return ThumbnailGenerator:Click(fileExtension, x, y, --[[hideSky = ]] false)
			end
		elseif object:IsA("LuaSourceContainer") then
			return ThumbnailGenerator:ClickTexture(baseUrl.. "Thumbs/Script.png", fileExtension, x, y)
		elseif object:IsA("SpecialMesh") then
			local part = Instance.new("Part")
			part.Parent = workspace
			object.Parent = part
			return ThumbnailGenerator:Click(fileExtension, x, y, --[[hideSky = ]] true)
		else
			pcall(function() object.Parent = workspace end)
		end
	end
end


local result, requestedUrls = ThumbnailGenerator:Click(fileExtension, x, y, --[[hideSky = ]] true)

return result, requestedUrls