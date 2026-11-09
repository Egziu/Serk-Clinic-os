import { useEffect, useState } from "react";
import { api } from "../api/client";
import { ErrorAlert } from "../components/common";
import { AssetPanel, type Asset } from "./SettingsPage";

export function SignaturePage() {
  const [asset, setAsset] = useState<Asset | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    api.get<Asset | null>("/api/v1/me/signature").then((r) => setAsset(r.data)).catch(setError);
  }, [version]);
  return (
    <>
      <div className="page-header">
        <div>
          <h1>My signature</h1>
          <p>Your handwritten signature image is placed above your electronic signature on issued documents only. Only you and clinic administrators can view it.</p>
        </div>
      </div>
      <ErrorAlert error={error} />
      <AssetPanel kind="signature" title="Signature image" hint="Sign on white paper, scan or photograph it, crop tightly." asset={asset} onUploaded={() => setVersion((v) => v + 1)} uploadUrl="/api/v1/me/signature" />
    </>
  );
}
