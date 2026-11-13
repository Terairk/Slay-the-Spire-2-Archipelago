// GoatCounter's count.js, loaded from index.html. It is absent until the script
// loads, and stays absent when a blocker stops it.
interface Window {
  goatcounter?: {
    count?: () => void;
  };
}
